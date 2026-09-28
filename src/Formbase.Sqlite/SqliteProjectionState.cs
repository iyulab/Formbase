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
/// same restart: a stamp recorded against a raw store that starts over would name positions the new
/// one reuses for different documents.
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
            verified           INTEGER NOT NULL
        );
        CREATE TABLE IF NOT EXISTS fb_projection_skips (
            form_type   TEXT    NOT NULL,
            ordinal     INTEGER NOT NULL,
            document_id TEXT    NOT NULL,
            reason      TEXT    NOT NULL,
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
        command.CommandText = "SELECT watermark, table_name, schema_fingerprint, verified FROM fb_projection_state WHERE form_type = $type";
        command.Parameters.AddWithValue("$type", type.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new ProjectionStamp(new Watermark(reader.GetInt64(0)), reader.GetString(1), reader.GetString(2), reader.GetInt64(3) != 0);
    }

    public async Task SetProjectedAsync(
        FormTypeRef type,
        ProjectionStamp stamp,
        IReadOnlyList<ProjectionSkip> skips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(skips);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // One transaction: a stamp without its skips would report a completed run with the previous
        // run's reasons for what it dropped.
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        await using (var upsert = connection.CreateCommand())
        {
            upsert.Transaction = transaction;
            upsert.CommandText =
                """
                INSERT INTO fb_projection_state (form_type, watermark, table_name, schema_fingerprint, verified)
                VALUES ($type, $watermark, $table, $fingerprint, $verified)
                ON CONFLICT (form_type) DO UPDATE
                    SET watermark = excluded.watermark,
                        table_name = excluded.table_name,
                        schema_fingerprint = excluded.schema_fingerprint,
                        verified = excluded.verified;
                DELETE FROM fb_projection_skips WHERE form_type = $type;
                """;
            upsert.Parameters.AddWithValue("$type", type.Value);
            upsert.Parameters.AddWithValue("$watermark", stamp.Watermark.Value);
            upsert.Parameters.AddWithValue("$table", stamp.TableName);
            upsert.Parameters.AddWithValue("$fingerprint", stamp.SchemaFingerprint);
            upsert.Parameters.AddWithValue("$verified", stamp.Verified ? 1 : 0);
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        if (skips.Count > 0)
        {
            await using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO fb_projection_skips (form_type, ordinal, document_id, reason) VALUES ($type, $ordinal, $document, $reason)";
            insert.Parameters.AddWithValue("$type", type.Value);
            var ordinal = insert.Parameters.AddWithValue("$ordinal", 0);
            var document = insert.Parameters.AddWithValue("$document", string.Empty);
            var reason = insert.Parameters.AddWithValue("$reason", string.Empty);

            for (var i = 0; i < skips.Count; i++)
            {
                ordinal.Value = i;
                document.Value = skips[i].DocumentId.Value.ToString("D");
                reason.Value = skips[i].Reason;
                await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT document_id, reason FROM fb_projection_skips WHERE form_type = $type ORDER BY ordinal";
        command.Parameters.AddWithValue("$type", type.Value);

        var skips = new List<ProjectionSkip>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            skips.Add(new ProjectionSkip(DocumentId.From(Guid.Parse(reader.GetString(0))), reader.GetString(1)));
        }

        return skips;
    }

    public async Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            DELETE FROM fb_projection_state WHERE form_type = $type;
            DELETE FROM fb_projection_skips WHERE form_type = $type;
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

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _database.EnsureAsync(Component, InitDdl, cancellationToken).ConfigureAwait(false);
        return await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
