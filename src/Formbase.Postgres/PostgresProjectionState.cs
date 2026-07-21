using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Formbase.Postgres;

/// <summary>
/// The durable <see cref="IProjectionState"/> — the ledger the engine writes when a projection
/// completes, kept in formbase's own Postgres schema alongside the raw store.
/// </summary>
/// <remarks>
/// <para>It lives here rather than in the projection store because the state is keyed by
/// <see cref="FormTypeRef"/>, and <c>FormType</c> is a formbase-internal concept that must not leak
/// into the backing database. Pair it with a durable <see cref="IFieldHintSource"/>: a query resolves
/// its table through the proposed schema, so durable state alone does not survive a restart.</para>
/// <para><c>updated_at</c> is write-only, deliberately: it is operator-facing forensic metadata (when
/// did this row last change), not a value the engine reads back. Do not mistake it for a live feature,
/// and do not delete it as dead code.</para>
/// </remarks>
public sealed class PostgresProjectionState : IProjectionState, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSchemaBootstrap _bootstrap;
    private readonly TimeProvider _clock;
    private readonly string _initDdl;

    /// <summary>
    /// Creates the state store over <paramref name="dataSource"/> (whose lifetime the caller owns),
    /// isolated in <paramref name="schema"/>. The schema and table are created on first use.
    /// </summary>
    public PostgresProjectionState(NpgsqlDataSource dataSource, string schema = "formbase", TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
        _bootstrap = new PostgresSchemaBootstrap(dataSource, schema);
        _clock = clock ?? TimeProvider.System;
        _initDdl =
            $"""
            CREATE SCHEMA IF NOT EXISTS "{_bootstrap.Schema}";
            CREATE TABLE IF NOT EXISTS "{_bootstrap.Schema}".projection_state (
                form_type  text PRIMARY KEY,
                watermark  bigint NOT NULL,
                updated_at timestamptz NOT NULL
            );
            """;
    }

    public async Task<Watermark?> GetProjectedWatermarkAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""SELECT watermark FROM "{_bootstrap.Schema}".projection_state WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is long watermark ? new Watermark(watermark) : null;
    }

    public async Task SetProjectedAsync(FormTypeRef type, Watermark watermark, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_bootstrap.Schema}".projection_state (form_type, watermark, updated_at)
            VALUES (@type, @watermark, @at)
            ON CONFLICT (form_type) DO UPDATE SET watermark = EXCLUDED.watermark, updated_at = EXCLUDED.updated_at
            """,
            connection);
        command.Parameters.AddWithValue("type", type.Value);
        command.Parameters.AddWithValue("watermark", watermark.Value);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = _clock.GetUtcNow() });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""DELETE FROM "{_bootstrap.Schema}".projection_state WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
        => _bootstrap.EnsureAsync(_initDdl, cancellationToken);

    /// <summary>Disposes the init gate. The injected data source is caller-owned and left untouched.</summary>
    public void Dispose() => _bootstrap.Dispose();
}
