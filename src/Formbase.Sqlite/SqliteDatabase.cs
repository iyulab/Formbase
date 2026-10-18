using System.Collections.Concurrent;
using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Formbase.Sqlite;

/// <summary>
/// One SQLite file shared by the raw store, the projection store, the projection state and the field hints. Owns what
/// every connection to it needs: the functions and collation that give the store the same answers as
/// the in-memory reference, and the one-time bootstrap of each component's tables.
/// </summary>
/// <remarks>
/// SQLite's own <c>LIKE</c> and <c>lower()</c> fold ASCII only, and it has no decimal type. Rather than
/// accept answers that differ from the other stores for non-ASCII text or for decimals stored as text,
/// the file registers <c>fb_contains</c>/<c>fb_startswith</c> (ordinal, case-insensitive — the .NET
/// rule the in-memory store uses) and the <c>fb_decimal</c> collation (numeric order and equality for
/// decimals kept as exact text). They are per connection in SQLite, so every connection gets them.
/// </remarks>
public sealed class SqliteDatabase : IDisposable
{
    private readonly string _connectionString;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly ConcurrentDictionary<string, bool> _initialized = new(StringComparer.Ordinal);

    /// <summary>A database over <paramref name="connectionString"/> (e.g. <c>Data Source=formbase.db</c>).</summary>
    public SqliteDatabase(string connectionString)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        _connectionString = connectionString;
    }

    /// <summary>The connection string this database opens — what two registrations are compared by.</summary>
    internal string ConnectionString => _connectionString;

    internal async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        connection.CreateFunction<string?, string?, bool?>(
            "fb_contains",
            static (text, part) => text is null || part is null ? null : text.Contains(part, StringComparison.OrdinalIgnoreCase),
            isDeterministic: true);
        connection.CreateFunction<string?, string?, bool?>(
            "fb_startswith",
            static (text, prefix) => text is null || prefix is null ? null : text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            isDeterministic: true);
        connection.CreateCollation(
            SqliteValues.DecimalCollation,
            static (x, y) => decimal.Parse(x, NumberStyles.Number, CultureInfo.InvariantCulture)
                .CompareTo(decimal.Parse(y, NumberStyles.Number, CultureInfo.InvariantCulture)));
        connection.CreateAggregate<string?, (decimal Sum, long Count), string?>(
            SqliteValues.DecimalSum,
            (0m, 0L),
            static (total, value) => Add(total, value),
            static total => total.Count == 0 ? null : total.Sum.ToString(CultureInfo.InvariantCulture),
            isDeterministic: true);
        connection.CreateAggregate<string?, (decimal Sum, long Count), string?>(
            SqliteValues.DecimalAverage,
            (0m, 0L),
            static (total, value) => Add(total, value),
            static total => total.Count == 0 ? null : (total.Sum / total.Count).ToString(CultureInfo.InvariantCulture),
            isDeterministic: true);

        return connection;

        // A value reaches the aggregate as its text — an integer as its digits, a decimal as stored.
        static (decimal Sum, long Count) Add((decimal Sum, long Count) total, string? value) =>
            value is null ? total : (total.Sum + decimal.Parse(value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture), total.Count + 1);
    }

    /// <summary>Runs <paramref name="ddl"/> once per process for <paramref name="component"/>.</summary>
    internal Task EnsureAsync(string component, string ddl, CancellationToken cancellationToken)
        => EnsureAsync(component, ddl, upgrade: null, cancellationToken);

    /// <summary>
    /// Runs <paramref name="ddl"/> and then <paramref name="upgrade"/> once per process for
    /// <paramref name="component"/>. The DDL creates what a new file needs; the upgrade brings a file
    /// created by an earlier version up to it (<c>CREATE TABLE IF NOT EXISTS</c> leaves an existing table as
    /// it was). The upgrade runs in an immediate transaction, so two processes opening one old file cannot
    /// both apply it — the second waits for the first and then finds nothing left to do.
    /// </summary>
    internal async Task EnsureAsync(
        string component,
        string ddl,
        Func<SqliteConnection, SqliteTransaction, CancellationToken, Task>? upgrade,
        CancellationToken cancellationToken)
    {
        if (_initialized.ContainsKey(component))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized.ContainsKey(component))
            {
                return;
            }

            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            // WAL lets a reader proceed while a projection rebuild writes — the shape a desktop process
            // with a UI reading and a background projector writing has.
            command.CommandText = "PRAGMA journal_mode=WAL;" + ddl;
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

            if (upgrade is not null)
            {
                await using var transaction = connection.BeginTransaction(deferred: false);
                await upgrade(connection, transaction, cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            _initialized[component] = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Disposes the bootstrap gate. Connections are opened per call and already closed.</summary>
    public void Dispose() => _gate.Dispose();
}
