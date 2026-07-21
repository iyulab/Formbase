using System.Text;
using System.Text.RegularExpressions;
using Npgsql;

namespace Formbase.Postgres;

/// <summary>
/// The one-time DDL bootstrap every store sharing a formbase Postgres schema runs through, and the
/// owner of the schema-scoped advisory lock they all serialize on.
/// </summary>
/// <remarks>
/// <para><c>CREATE ... IF NOT EXISTS</c> is not atomic against the catalog, so two stores — or two
/// processes — cold-starting against the same schema can both decide to create it and one throws on a
/// duplicate object. A per-store lock would not help: the racers are different objects. The key is
/// derived from the schema name, so every store in a schema takes the <i>same</i> lock, and the raw
/// store's append serialization uses that key too.</para>
/// <para>The schema name is the one caller-supplied value that gets interpolated into SQL (an
/// identifier cannot be a parameter), so it is validated here, once, before any command can carry it.</para>
/// </remarks>
internal sealed partial class PostgresSchemaBootstrap : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _initialized;
    private volatile bool _disposed;

    /// <summary>
    /// Validates <paramref name="schema"/> and derives its advisory lock key. Touches no server —
    /// the DDL runs lazily on the first <see cref="EnsureAsync"/>.
    /// </summary>
    internal PostgresSchemaBootstrap(NpgsqlDataSource dataSource, string schema)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (!SchemaNamePattern().IsMatch(schema))
        {
            throw new ArgumentException($"Schema name '{schema}' is not a valid PostgreSQL identifier.", nameof(schema));
        }

        _dataSource = dataSource;
        Schema = schema;
        AdvisoryLockKey = StableKey(schema);
    }

    /// <summary>The validated schema every statement quotes as an identifier.</summary>
    internal string Schema { get; }

    /// <summary>The key every store in this schema locks on — DDL and serialized appends alike.</summary>
    internal long AdvisoryLockKey { get; }

    /// <summary>Runs <paramref name="ddl"/> at most once per instance, under the schema's advisory lock.</summary>
    internal async ValueTask EnsureAsync(string ddl, CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", connection, transaction))
            {
                lockCommand.Parameters.AddWithValue("k", AdvisoryLockKey);
                await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            }

            await using (var command = new NpgsqlCommand(ddl, connection, transaction))
            {
                await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>A stable 64-bit key (FNV-1a) so each schema serializes independently of every other.</summary>
    private static long StableKey(string schema)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in Encoding.UTF8.GetBytes(schema))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return unchecked((long)hash);
    }

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SchemaNamePattern();

    /// <summary>
    /// Disposes the init gate. The injected data source is caller-owned and left untouched. Idempotent:
    /// <see cref="PostgresFieldHintSource"/> is registered both concretely and behind
    /// <see cref="Formbase.Core.Ports.IFieldHintSource"/> (see
    /// <c>PostgresServiceCollectionExtensions.AddPostgresFieldHints</c>), so the container may resolve
    /// and dispose the same instance twice — this guard makes that explicitly safe rather than relying
    /// on <see cref="SemaphoreSlim.Dispose()"/> happening to tolerate a second call.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gate.Dispose();
    }
}
