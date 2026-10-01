using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Microsoft.Data.Sqlite;

namespace Formbase.Sqlite;

/// <summary>
/// The durable <see cref="IProjectionState"/> in the same SQLite file as the projected tables, so a
/// restarted process knows which projections are current without rebuilding them.
/// </summary>
/// <remarks>
/// The stamp's watermark is the raw store's. Pair this with a raw store whose watermarks survive the
/// same restart — <see cref="SqliteRawStore"/> in the same file is one: a stamp recorded against a raw
/// store that starts over would name positions the new one reuses for different documents.
/// </remarks>
public sealed class SqliteProjectionState : IProjectionState
{
    private const string Component = "projection-state";

    private const string InitDdl =
        """
        CREATE TABLE IF NOT EXISTS fb_projection_state (
            form_type          TEXT    PRIMARY KEY,
            watermark          INTEGER NOT NULL,
            table_name         TEXT    NOT NULL,
            schema_fingerprint TEXT    NOT NULL,
            verified           INTEGER NOT NULL,
            skips_keyed        INTEGER NOT NULL DEFAULT 0
        );
        CREATE TABLE IF NOT EXISTS fb_projection_skips (
            form_type   TEXT    NOT NULL,
            ordinal     INTEGER NOT NULL,
            document_id TEXT    NOT NULL,
            reason      TEXT    NOT NULL,
            record_key  TEXT    NULL,
            PRIMARY KEY (form_type, ordinal)
        );
        CREATE TABLE IF NOT EXISTS fb_projection_field_skips (
            form_type   TEXT    NOT NULL,
            ordinal     INTEGER NOT NULL,
            document_id TEXT    NOT NULL,
            field       TEXT    NOT NULL,
            reason      TEXT    NOT NULL,
            record_key  TEXT    NULL,
            PRIMARY KEY (form_type, ordinal)
        );
        """;

    private readonly SqliteDatabase _database;

    public SqliteProjectionState(SqliteDatabase database)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
    }

    public async Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT watermark, table_name, schema_fingerprint, verified, skips_keyed FROM fb_projection_state WHERE form_type = $type";
        command.Parameters.AddWithValue("$type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProjectionStamp(new Watermark(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0, reader.GetInt64(4) != 0);
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // One transaction: a stamp without its skips would report a completed run with the previous
        // run's reasons for what it dropped.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO fb_projection_state (form_type, watermark, table_name, schema_fingerprint, verified, skips_keyed)
                VALUES ($type, $watermark, $table, $fingerprint, $verified, $keyed)
                ON CONFLICT (form_type) DO UPDATE
                    SET watermark = excluded.watermark,
                        table_name = excluded.table_name,
                        schema_fingerprint = excluded.schema_fingerprint,
                        verified = excluded.verified,
                        skips_keyed = excluded.skips_keyed;
                DELETE FROM fb_projection_skips WHERE form_type = $type;
                DELETE FROM fb_projection_field_skips WHERE form_type = $type;
                """;
            BindStamp(upsert, type, stamp);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await InsertSkipsAsync(connection, transaction, type, skips, fieldSkips, firstOrdinal: 0, cancellationToken).ConfigureAwait(false);
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

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Immediate: the compare-and-set reads the stamp it replaces, and a deferred transaction would
        // let another writer move it between the read and the write.
        await using var transaction = connection.BeginTransaction(deferred: false);

        await using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText =
                """
                UPDATE fb_projection_state
                    SET watermark = $watermark, table_name = $table, schema_fingerprint = $fingerprint,
                        verified = $verified, skips_keyed = $keyed
                    WHERE form_type = $type AND watermark = $expected AND verified = 1
                """;
            BindStamp(update, type, stamp);
            update.Parameters.AddWithValue("$expected", expectedWatermark.Value);
            if (await update.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 0)
            {
                return false;
            }
        }

        var withdrawn = withdrawnKeys.Select(k => (Column: "record_key", Value: k.Value))
            .Concat(withdrawnDocuments.Select(d => (Column: "document_id", Value: d.Value.ToString("D"))))
            .ToList();
        foreach (var table in new[] { "fb_projection_skips", "fb_projection_field_skips" })
        {
            foreach (var chunk in withdrawn.Chunk(WithdrawChunk))
            {
                await using var delete = connection.CreateCommand();
                delete.Transaction = transaction;
                delete.CommandText = $"DELETE FROM {table} WHERE form_type = $type AND ({string.Join(" OR ", chunk.Select((w, i) => $"{w.Column} = $w{i}"))})";
                delete.Parameters.AddWithValue("$type", type.Value);
                for (var i = 0; i < chunk.Length; i++)
                {
                    delete.Parameters.AddWithValue($"$w{i}", chunk[i].Value);
                }

                await delete.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        // Appended after what is left, in order. The ordinals count on from the highest left rather
        // than closing the gaps withdrawal opened: only their order is read.
        long next;
        await using (var max = connection.CreateCommand())
        {
            max.Transaction = transaction;
            max.CommandText =
                """
                SELECT MAX(COALESCE((SELECT MAX(ordinal) FROM fb_projection_skips WHERE form_type = $type), -1),
                           COALESCE((SELECT MAX(ordinal) FROM fb_projection_field_skips WHERE form_type = $type), -1)) + 1
                """;
            max.Parameters.AddWithValue("$type", type.Value);
            next = (long)(await max.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        }

        await InsertSkipsAsync(connection, transaction, type, addedSkips, addedFieldSkips, next, cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    // Each withdrawn key or document is one bound parameter; well under SQLite's per-statement limit.
    private const int WithdrawChunk = 500;

    private static void BindStamp(SqliteCommand command, FormTypeRef type, ProjectionStamp stamp)
    {
        command.Parameters.AddWithValue("$type", type.Value);
        command.Parameters.AddWithValue("$watermark", stamp.Watermark.Value);
        command.Parameters.AddWithValue("$table", stamp.TableName);
        command.Parameters.AddWithValue("$fingerprint", stamp.SchemaFingerprint);
        command.Parameters.AddWithValue("$verified", stamp.Verified ? 1 : 0);
        command.Parameters.AddWithValue("$keyed", stamp.SkipsKeyed ? 1 : 0);
    }

    private static async Task InsertSkipsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        FormTypeRef type,
        IReadOnlyList<ProjectionSkip> skips,
        IReadOnlyList<ProjectionFieldSkip> fieldSkips,
        long firstOrdinal,
        CancellationToken cancellationToken)
    {
        if (skips.Count > 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO fb_projection_skips (form_type, ordinal, document_id, reason, record_key) VALUES ($type, $ordinal, $document, $reason, $key)";
            insert.Parameters.AddWithValue("$type", type.Value);
            var ordinal = insert.Parameters.AddWithValue("$ordinal", 0L);
            var document = insert.Parameters.AddWithValue("$document", string.Empty);
            var reason = insert.Parameters.AddWithValue("$reason", string.Empty);
            var key = insert.Parameters.AddWithValue("$key", DBNull.Value);

            for (var i = 0; i < skips.Count; i++)
            {
                ordinal.Value = firstOrdinal + i;
                document.Value = skips[i].DocumentId.Value.ToString("D");
                reason.Value = skips[i].Reason;
                key.Value = (object?)skips[i].Key?.Value ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        if (fieldSkips.Count > 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO fb_projection_field_skips (form_type, ordinal, document_id, field, reason, record_key) VALUES ($type, $ordinal, $document, $field, $reason, $key)";
            insert.Parameters.AddWithValue("$type", type.Value);
            var ordinal = insert.Parameters.AddWithValue("$ordinal", 0L);
            var document = insert.Parameters.AddWithValue("$document", string.Empty);
            var field = insert.Parameters.AddWithValue("$field", string.Empty);
            var reason = insert.Parameters.AddWithValue("$reason", string.Empty);
            var key = insert.Parameters.AddWithValue("$key", DBNull.Value);

            for (var i = 0; i < fieldSkips.Count; i++)
            {
                ordinal.Value = firstOrdinal + i;
                document.Value = fieldSkips[i].DocumentId.Value.ToString("D");
                field.Value = fieldSkips[i].Field;
                reason.Value = fieldSkips[i].Reason;
                key.Value = (object?)fieldSkips[i].Key?.Value ?? DBNull.Value;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id, reason, record_key FROM fb_projection_skips WHERE form_type = $type ORDER BY ordinal";
        command.Parameters.AddWithValue("$type", type.Value);

        var skips = new List<ProjectionSkip>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            skips.Add(new ProjectionSkip(DocumentId.From(Guid.Parse(reader.GetString(0))), reader.GetString(1), ReadKey(reader, 2)));
        }

        return skips;
    }

    public async Task<IReadOnlyList<ProjectionFieldSkip>> GetFieldSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id, field, reason, record_key FROM fb_projection_field_skips WHERE form_type = $type ORDER BY ordinal";
        command.Parameters.AddWithValue("$type", type.Value);

        var fieldSkips = new List<ProjectionFieldSkip>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            fieldSkips.Add(new ProjectionFieldSkip(DocumentId.From(Guid.Parse(reader.GetString(0))), reader.GetString(1), reader.GetString(2), ReadKey(reader, 3)));
        }

        return fieldSkips;
    }

    public async Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM fb_projection_state WHERE form_type = $type;
            DELETE FROM fb_projection_skips WHERE form_type = $type;
            DELETE FROM fb_projection_field_skips WHERE form_type = $type;
            """;
        command.Parameters.AddWithValue("$type", type.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        // Touches nothing when the type was never projected — the required no-op.
        command.CommandText = "UPDATE fb_projection_state SET verified = 0 WHERE form_type = $type";
        command.Parameters.AddWithValue("$type", type.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static RecordKey? ReadKey(SqliteDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : RecordKey.Create(reader.GetString(ordinal));

    /// <summary>
    /// Brings state tables created before skips carried record keys up to the current shape. A stamp
    /// from then reads <see cref="ProjectionStamp.SkipsKeyed"/> false and its skips carry no key, so the
    /// next projection rebuilds rather than bringing the table forward on skips it cannot attribute.
    /// </summary>
    private static async Task UpgradeAsync(SqliteConnection connection, SqliteTransaction transaction, CancellationToken cancellationToken)
    {
        foreach (var (table, column, definition) in new[]
        {
            ("fb_projection_state", "skips_keyed", "INTEGER NOT NULL DEFAULT 0"),
            ("fb_projection_skips", "record_key", "TEXT NULL"),
            ("fb_projection_field_skips", "record_key", "TEXT NULL"),
        })
        {
            await using var info = connection.CreateCommand();
            info.Transaction = transaction;
            info.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
            if ((long)(await info.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))! > 0)
            {
                continue;
            }

            await using var alter = connection.CreateCommand();
            alter.Transaction = transaction;
            alter.CommandText = $"ALTER TABLE {table} ADD COLUMN {column} {definition}";
            await alter.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _database.EnsureAsync(Component, InitDdl, UpgradeAsync, cancellationToken).ConfigureAwait(false);
        return await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
