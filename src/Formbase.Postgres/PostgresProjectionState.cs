using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
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
                form_type          text PRIMARY KEY,
                watermark          bigint NOT NULL,
                table_name         text NOT NULL,
                schema_fingerprint text NOT NULL,
                updated_at         timestamptz NOT NULL
            );
            -- Integrity axis (tri-state). Added by migration so a table from before this column
            -- gains it with every existing row defaulting to verified — those rows recorded
            -- completed projections, so true is the honest default.
            ALTER TABLE "{_bootstrap.Schema}".projection_state
                ADD COLUMN IF NOT EXISTS verified boolean NOT NULL DEFAULT true;
            -- What the last completed projection dropped, and why. Keyed by form type like the
            -- stamp and replaced with it, because the two describe the same run: `ordinal` keeps the
            -- order the projector produced so a reader sees the same sequence twice.
            CREATE TABLE IF NOT EXISTS "{_bootstrap.Schema}".projection_skips (
                form_type   text   NOT NULL,
                ordinal     int    NOT NULL,
                document_id uuid   NOT NULL,
                reason      text   NOT NULL,
                PRIMARY KEY (form_type, ordinal)
            );
            """;
    }

    public async Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""SELECT watermark, table_name, schema_fingerprint, verified FROM "{_bootstrap.Schema}".projection_state WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProjectionStamp(new Watermark(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3));
    }

    public async Task SetProjectedAsync(
        FormTypeRef type,
        ProjectionStamp stamp,
        IReadOnlyList<ProjectionSkip> skips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(skips);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // One transaction, because a stamp without its skips is the state this whole record exists to
        // prevent: a status surface that says the run completed while the reasons it dropped rows are
        // the previous run's, or missing.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_bootstrap.Schema}".projection_state (form_type, watermark, table_name, schema_fingerprint, verified, updated_at)
            VALUES (@type, @watermark, @table, @fingerprint, @verified, @at)
            ON CONFLICT (form_type) DO UPDATE
                SET watermark = EXCLUDED.watermark,
                    table_name = EXCLUDED.table_name,
                    schema_fingerprint = EXCLUDED.schema_fingerprint,
                    verified = EXCLUDED.verified,
                    updated_at = EXCLUDED.updated_at
            """,
            connection);
        command.Parameters.AddWithValue("type", type.Value);
        command.Parameters.AddWithValue("watermark", stamp.Watermark.Value);
        command.Parameters.AddWithValue("table", stamp.TableName);
        command.Parameters.AddWithValue("fingerprint", stamp.SchemaFingerprint);
        command.Parameters.AddWithValue("verified", stamp.Verified);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = _clock.GetUtcNow() });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await ReplaceSkipsAsync(connection, type, skips, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ReplaceSkipsAsync(
        NpgsqlConnection connection,
        FormTypeRef type,
        IReadOnlyList<ProjectionSkip> skips,
        CancellationToken cancellationToken)
    {
        await using (var delete = new NpgsqlCommand(
            $"""DELETE FROM "{_bootstrap.Schema}".projection_skips WHERE form_type = @type""",
            connection))
        {
            delete.Parameters.AddWithValue("type", type.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (skips.Count == 0)
        {
            return;
        }

        // Binary copy rather than a parameterized insert per row: a badly mapped run skips every
        // document it read, so the row count here tracks the size of the intake, not the size of the
        // failure.
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""COPY "{_bootstrap.Schema}".projection_skips (form_type, ordinal, document_id, reason) FROM STDIN (FORMAT BINARY)""",
            cancellationToken).ConfigureAwait(false);

        for (var ordinal = 0; ordinal < skips.Count; ordinal++)
        {
            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(type.Value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(ordinal, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(skips[ordinal].DocumentId.Value, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(skips[ordinal].Reason, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT document_id, reason FROM "{_bootstrap.Schema}".projection_skips
                WHERE form_type = @type
                ORDER BY ordinal
            """,
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        var skips = new List<ProjectionSkip>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            skips.Add(new ProjectionSkip(DocumentId.From(reader.GetGuid(0)), reader.GetString(1)));
        }

        return skips;
    }

    public async Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            DELETE FROM "{_bootstrap.Schema}".projection_state WHERE form_type = @type;
            DELETE FROM "{_bootstrap.Schema}".projection_skips WHERE form_type = @type;
            """,
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // UPDATE touches nothing when no row exists — the required no-op when a type was never projected.
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE "{_bootstrap.Schema}".projection_state
                SET verified = false, updated_at = @at
                WHERE form_type = @type
            """,
            connection);
        command.Parameters.AddWithValue("type", type.Value);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = _clock.GetUtcNow() });

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
        => _bootstrap.EnsureAsync(_initDdl, cancellationToken);

    /// <summary>Disposes the init gate. The injected data source is caller-owned and left untouched.</summary>
    public void Dispose() => _bootstrap.Dispose();
}
