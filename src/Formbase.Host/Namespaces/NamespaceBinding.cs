using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Host.Composition;
using Npgsql;

namespace Formbase.Host.Namespaces;

/// <summary>
/// The durable store's own record of which namespace it holds, and the check that this host serves
/// that one.
/// <para>
/// A namespace name is host configuration; the data it names lives in a PostgreSQL schema. Without a
/// record in the schema itself, two hosts configured with different names over the same schema both
/// start, both pass the namespace selector, and serve each other's documents under their own names —
/// the exact outcome the selector's 404 exists to prevent, happening one layer below it. So the
/// schema remembers the name it was first used under, and a host serving another name is refused.
/// </para>
/// <para>
/// A schema that holds no record yet — created before this check existed, or brand new — is claimed
/// by the first host to verify it. Nothing changes for a deployment that runs one namespace per
/// schema; a deployment already running two names over one schema is the one this refuses, because
/// its data is already being mixed.
/// </para>
/// <para>
/// The check is made at startup and again, until it succeeds, ahead of every request and readiness
/// probe: a host whose database was down when it started would otherwise be first to write once the
/// database came back, having verified nothing. Once it succeeds it is not repeated: the row is
/// written once and nothing in the host rewrites it.
/// </para>
/// </summary>
internal sealed partial class NamespaceBinding : IDisposable
{
    private static readonly FormTypeRef Probe = FormTypeRef.Create("formbase-namespace-probe");

    private readonly NpgsqlDataSource _dataSource;
    private readonly IProjectionState _state;
    private readonly string _schema;
    private readonly string _name;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<NamespaceBinding> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile bool _bound;
    private volatile NamespaceBindingConflictException? _conflict;

    public NamespaceBinding(
        NpgsqlDataSource dataSource,
        IProjectionState state,
        StoreProfileSelection profile,
        NamespaceSelector selector,
        IHostApplicationLifetime lifetime,
        ILogger<NamespaceBinding> logger)
    {
        _dataSource = dataSource;
        _state = state;
        _lifetime = lifetime;
        _logger = logger;
        _schema = profile.Location?.Schema
            ?? throw new InvalidOperationException("A namespace binding needs a durable store location.");
        _name = selector.Name;
    }

    /// <summary>
    /// Returns once the schema is known to hold this host's namespace, claiming it if it holds none.
    /// Throws <see cref="NamespaceBindingConflictException"/> when it holds another — and keeps
    /// throwing it without asking again, since a conflict does not resolve itself.
    /// </summary>
    public async ValueTask EnsureAsync(CancellationToken cancellationToken)
    {
        if (_bound)
        {
            return;
        }

        if (_conflict is { } known)
        {
            throw known;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_bound)
            {
                return;
            }

            if (_conflict is { } found)
            {
                throw found;
            }

            // The schema is created by the stores' own bootstrap, under the lock every store in it
            // shares. Creating it here too, under a different lock, is the concurrent CREATE SCHEMA
            // the bootstrap exists to serialize — so a harmless read makes the stores create it
            // first, and this only ever adds a table to a schema that exists.
            await _state.GetAsync(Probe, cancellationToken).ConfigureAwait(false);

            var holder = await ClaimAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(holder, _name, StringComparison.Ordinal))
            {
                _conflict = new NamespaceBindingConflictException(_schema, holder, _name);
                LogConflict(_logger, _schema, holder, _name);

                // Found after startup — the database was down when the host started. The host stops
                // rather than keep answering 503 forever: the same configuration refused at startup
                // would never have run, and a stopped process is what an orchestrator shows.
                if (_lifetime.ApplicationStarted.IsCancellationRequested)
                {
                    _lifetime.StopApplication();
                }

                throw _conflict;
            }

            _bound = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Records this host's namespace if the schema holds none, and returns whichever one it holds.
    /// Serialized per schema, so two hosts claiming a fresh schema at once agree on one winner.
    /// </summary>
    private async Task<string> ClaimAsync(CancellationToken cancellationToken)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var lockCommand = new NpgsqlCommand(
            "SELECT pg_advisory_xact_lock(hashtext('formbase:namespace-binding'), hashtext(@schema))",
            connection,
            transaction))
        {
            lockCommand.Parameters.AddWithValue("schema", _schema);
            await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        // One row, enforced by the key: a schema holds one namespace or none.
        await using (var claim = new NpgsqlCommand(
            $"""
            CREATE TABLE IF NOT EXISTS "{_schema}".namespace_binding (
                singleton boolean PRIMARY KEY DEFAULT true CHECK (singleton),
                namespace text NOT NULL,
                bound_at timestamptz NOT NULL DEFAULT now()
            );
            INSERT INTO "{_schema}".namespace_binding (namespace) VALUES (@name) ON CONFLICT DO NOTHING;
            """,
            connection,
            transaction))
        {
            claim.Parameters.AddWithValue("name", _name);
            await claim.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using var read = new NpgsqlCommand(
            $"""SELECT namespace FROM "{_schema}".namespace_binding""", connection, transaction);
        var holder = (string)(await read.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return holder;
    }

    public void Dispose() => _gate.Dispose();

    [LoggerMessage(Level = LogLevel.Critical,
        Message = "The PostgreSQL schema '{Schema}' holds the namespace '{Holder}', and this host serves '{Requested}'.")]
    private static partial void LogConflict(ILogger logger, string schema, string holder, string requested);
}

/// <summary>
/// The schema a durable host was composed with holds another namespace's data. A configuration
/// error, not an outage: nothing but a change of <c>Formbase:Schema</c> or <c>Formbase:Namespace</c>
/// resolves it.
/// </summary>
internal sealed class NamespaceBindingConflictException(string schema, string holder, string requested)
    : Exception(
        $"The PostgreSQL schema '{schema}' holds the namespace '{holder}', and this host serves '{requested}'. " +
        "Each namespace needs its own schema (and its own MorphDB project): set Formbase:Schema, " +
        $"or serve '{holder}' from this host.")
{
    public string Schema { get; } = schema;

    public string Holder { get; } = holder;
}
