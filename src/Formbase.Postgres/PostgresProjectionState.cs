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
            -- Optional fields the same run emptied in rows it did project — replaced with the stamp
            -- for the same reason as the skips above.
            CREATE TABLE IF NOT EXISTS "{_bootstrap.Schema}".projection_field_skips (
                form_type   text   NOT NULL,
                ordinal     int    NOT NULL,
                document_id uuid   NOT NULL,
                field       text   NOT NULL,
                reason      text   NOT NULL,
                PRIMARY KEY (form_type, ordinal)
            );
            -- Which record each skip's document stood for, so a later run can withdraw what a corrected
            -- record left behind without rebuilding; and whether a stamp's skips carry it. Added by
            -- migration with false and NULL: skips recorded before then cannot be attributed, so the
            -- next projection over such a stamp rebuilds.
            ALTER TABLE "{_bootstrap.Schema}".projection_state
                ADD COLUMN IF NOT EXISTS skips_keyed boolean NOT NULL DEFAULT false;
            ALTER TABLE "{_bootstrap.Schema}".projection_skips
                ADD COLUMN IF NOT EXISTS record_key text NULL;
            ALTER TABLE "{_bootstrap.Schema}".projection_field_skips
                ADD COLUMN IF NOT EXISTS record_key text NULL;
            """;
    }

    public async Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""SELECT watermark, table_name, schema_fingerprint, verified, skips_keyed FROM "{_bootstrap.Schema}".projection_state WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProjectionStamp(new Watermark(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetBoolean(3), reader.GetBoolean(4));
    }

    public async Task SetProjectedAsync(
        FormTypeRef type,
        ProjectionStamp stamp,
        IReadOnlyList<ProjectionSkip> skips,
        IReadOnlyList<ProjectionFieldSkip> fieldSkips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(skips);
        ArgumentNullException.ThrowIfNull(fieldSkips);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // One transaction, because a stamp without its skips is the state this whole record exists to
        // prevent: a status surface that says the run completed while the reasons it dropped rows are
        // the previous run's, or missing.
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO "{_bootstrap.Schema}".projection_state (form_type, watermark, table_name, schema_fingerprint, verified, skips_keyed, updated_at)
            VALUES (@type, @watermark, @table, @fingerprint, @verified, @keyed, @at)
            ON CONFLICT (form_type) DO UPDATE
                SET watermark = EXCLUDED.watermark,
                    table_name = EXCLUDED.table_name,
                    schema_fingerprint = EXCLUDED.schema_fingerprint,
                    verified = EXCLUDED.verified,
                    skips_keyed = EXCLUDED.skips_keyed,
                    updated_at = EXCLUDED.updated_at
            """,
            connection);
        BindStamp(command, type, stamp);

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        await using (var delete = new NpgsqlCommand(
            $"""
            DELETE FROM "{_bootstrap.Schema}".projection_skips WHERE form_type = @type;
            DELETE FROM "{_bootstrap.Schema}".projection_field_skips WHERE form_type = @type;
            """,
            connection))
        {
            delete.Parameters.AddWithValue("type", type.Value);
            await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await WriteSkipsAsync(connection, type, skips, firstOrdinal: 0, cancellationToken).ConfigureAwait(false);
        await WriteFieldSkipsAsync(connection, type, fieldSkips, firstOrdinal: 0, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<bool> ApplyProjectedDeltaAsync(
        FormTypeRef type,
        Watermark expectedWatermark,
        ProjectionStamp stamp,
        IReadOnlyCollection<RecordKey> withdrawnKeys,
        IReadOnlyCollection<DocumentId> withdrawnDocuments,
        IReadOnlyList<ProjectionSkip> addedSkips,
        IReadOnlyList<ProjectionFieldSkip> addedFieldSkips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(withdrawnKeys);
        ArgumentNullException.ThrowIfNull(withdrawnDocuments);
        ArgumentNullException.ThrowIfNull(addedSkips);
        ArgumentNullException.ThrowIfNull(addedFieldSkips);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // The compare-and-set: the row update takes the row lock, so a second delta from the same stamp
        // waits here and then finds the watermark moved.
        await using (var update = new NpgsqlCommand(
            $"""
            UPDATE "{_bootstrap.Schema}".projection_state
                SET watermark = @watermark, table_name = @table, schema_fingerprint = @fingerprint,
                    verified = @verified, skips_keyed = @keyed, updated_at = @at
                WHERE form_type = @type AND watermark = @expected AND verified
            """,
            connection))
        {
            BindStamp(update, type, stamp);
            update.Parameters.AddWithValue("expected", expectedWatermark.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return false;
            }
        }

        await using (var withdraw = new NpgsqlCommand(
            $"""
            DELETE FROM "{_bootstrap.Schema}".projection_skips
                WHERE form_type = @type AND (record_key = ANY(@keys) OR document_id = ANY(@documents));
            DELETE FROM "{_bootstrap.Schema}".projection_field_skips
                WHERE form_type = @type AND (record_key = ANY(@keys) OR document_id = ANY(@documents));
            """,
            connection))
        {
            withdraw.Parameters.AddWithValue("type", type.Value);
            withdraw.Parameters.Add(new NpgsqlParameter("keys", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = withdrawnKeys.Select(k => k.Value).ToArray() });
            withdraw.Parameters.Add(new NpgsqlParameter("documents", NpgsqlDbType.Array | NpgsqlDbType.Uuid) { Value = withdrawnDocuments.Select(d => d.Value).ToArray() });
            await withdraw.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        // Appended after what is left, in order; the ordinals count on from the highest left rather
        // than closing the gaps withdrawal opened, since only their order is read.
        int next;
        await using (var max = new NpgsqlCommand(
            $"""
            SELECT GREATEST(
                COALESCE((SELECT MAX(ordinal) FROM "{_bootstrap.Schema}".projection_skips WHERE form_type = @type), -1),
                COALESCE((SELECT MAX(ordinal) FROM "{_bootstrap.Schema}".projection_field_skips WHERE form_type = @type), -1)) + 1
            """,
            connection))
        {
            max.Parameters.AddWithValue("type", type.Value);
            next = (int)(await max.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await WriteSkipsAsync(connection, type, addedSkips, next, cancellationToken).ConfigureAwait(false);
        await WriteFieldSkipsAsync(connection, type, addedFieldSkips, next, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private void BindStamp(NpgsqlCommand command, FormTypeRef type, ProjectionStamp stamp)
    {
        command.Parameters.AddWithValue("type", type.Value);
        command.Parameters.AddWithValue("watermark", stamp.Watermark.Value);
        command.Parameters.AddWithValue("table", stamp.TableName);
        command.Parameters.AddWithValue("fingerprint", stamp.SchemaFingerprint);
        command.Parameters.AddWithValue("verified", stamp.Verified);
        command.Parameters.AddWithValue("keyed", stamp.SkipsKeyed);
        command.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = _clock.GetUtcNow() });
    }

    private async Task WriteSkipsAsync(
        NpgsqlConnection connection,
        FormTypeRef type,
        IReadOnlyList<ProjectionSkip> skips,
        int firstOrdinal,
        CancellationToken cancellationToken)
    {
        if (skips.Count == 0)
        {
            return;
        }

        // Binary copy rather than a parameterized insert per row: a badly mapped run skips every
        // document it read, so the row count here tracks the size of the intake, not the size of the
        // failure.
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""COPY "{_bootstrap.Schema}".projection_skips (form_type, ordinal, document_id, reason, record_key) FROM STDIN (FORMAT BINARY)""",
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < skips.Count; i++)
        {
            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(type.Value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(firstOrdinal + i, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(skips[i].DocumentId.Value, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(skips[i].Reason, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteKeyAsync(writer, skips[i].Key, cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task WriteFieldSkipsAsync(
        NpgsqlConnection connection,
        FormTypeRef type,
        IReadOnlyList<ProjectionFieldSkip> fieldSkips,
        int firstOrdinal,
        CancellationToken cancellationToken)
    {
        if (fieldSkips.Count == 0)
        {
            return;
        }

        // Binary copy for the same reason as the skips: one badly typed optional column empties a
        // field in every row, so this tracks the intake's size too.
        await using var writer = await connection.BeginBinaryImportAsync(
            $"""COPY "{_bootstrap.Schema}".projection_field_skips (form_type, ordinal, document_id, field, reason, record_key) FROM STDIN (FORMAT BINARY)""",
            cancellationToken).ConfigureAwait(false);

        for (var i = 0; i < fieldSkips.Count; i++)
        {
            await writer.StartRowAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(type.Value, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(firstOrdinal + i, NpgsqlDbType.Integer, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(fieldSkips[i].DocumentId.Value, NpgsqlDbType.Uuid, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(fieldSkips[i].Field, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(fieldSkips[i].Reason, NpgsqlDbType.Text, cancellationToken).ConfigureAwait(false);
            await WriteKeyAsync(writer, fieldSkips[i].Key, cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteKeyAsync(NpgsqlBinaryImporter writer, RecordKey? key, CancellationToken cancellationToken) =>
        key is { } k
            ? writer.WriteAsync(k.Value, NpgsqlDbType.Text, cancellationToken)
            : writer.WriteNullAsync(cancellationToken);

    public async Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT document_id, reason, record_key FROM "{_bootstrap.Schema}".projection_skips
                WHERE form_type = @type
                ORDER BY ordinal
            """,
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        var skips = new List<ProjectionSkip>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            skips.Add(new ProjectionSkip(DocumentId.From(reader.GetGuid(0)), reader.GetString(1), ReadKey(reader, 2)));
        }

        return skips;
    }

    public async Task<IReadOnlyList<ProjectionFieldSkip>> GetFieldSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT document_id, field, reason, record_key FROM "{_bootstrap.Schema}".projection_field_skips
                WHERE form_type = @type
                ORDER BY ordinal
            """,
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        var fieldSkips = new List<ProjectionFieldSkip>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fieldSkips.Add(new ProjectionFieldSkip(DocumentId.From(reader.GetGuid(0)), reader.GetString(1), reader.GetString(2), ReadKey(reader, 3)));
        }

        return fieldSkips;
    }

    private static RecordKey? ReadKey(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : RecordKey.Create(reader.GetString(ordinal));

    public async Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            DELETE FROM "{_bootstrap.Schema}".projection_state WHERE form_type = @type;
            DELETE FROM "{_bootstrap.Schema}".projection_skips WHERE form_type = @type;
            DELETE FROM "{_bootstrap.Schema}".projection_field_skips WHERE form_type = @type;
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
