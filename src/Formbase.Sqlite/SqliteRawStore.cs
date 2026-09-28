using System.Globalization;
using System.Runtime.CompilerServices;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Microsoft.Data.Sqlite;

namespace Formbase.Sqlite;

/// <summary>
/// The durable, append-only <see cref="IRawStore"/> in a SQLite file — the same file as the projection
/// side when both are registered over one connection string, so raw positions and the projection stamps
/// that name them are kept, and lost, together. Watermarks are globally monotonic across form types, as
/// on the in-memory reference and PostgreSQL stores.
/// </summary>
/// <remarks>
/// <para><b>Watermarks are never reused.</b> They come from an <c>AUTOINCREMENT</c> key, which SQLite
/// guarantees never to hand out twice in one file, even after rows are removed. A projection stamp names
/// a raw position; a position reused for a different document would make an old stamp read as current.
/// Delete the file to start over — the projection state goes with it.</para>
/// <para><b>Appends are serialized by the file.</b> Each append runs in an immediate transaction, which
/// takes SQLite's write lock before reading, so watermark order is commit order across every connection
/// and process using the file, and the check for an existing id cannot race the insert.</para>
/// </remarks>
public sealed class SqliteRawStore : IRawStore
{
    private const string Component = "raw";

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    private const string InitDdl =
        """
        CREATE TABLE IF NOT EXISTS fb_raw_documents (
            watermark   INTEGER PRIMARY KEY AUTOINCREMENT,
            id          TEXT    NOT NULL UNIQUE,
            form_type   TEXT    NOT NULL,
            body        TEXT    NOT NULL,
            appended_at TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_fb_raw_documents_type_watermark ON fb_raw_documents (form_type, watermark);
        """;

    private const string SelectColumns = "SELECT id, form_type, body, watermark, appended_at FROM fb_raw_documents";

    private readonly SqliteDatabase _database;
    private readonly TimeProvider _clock;

    public SqliteRawStore(SqliteDatabase database, TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(database);
        _database = database;
        _clock = clock ?? TimeProvider.System;
    }

    public async Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);

        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        // Immediate, not deferred: the write lock is taken before the id check reads, so a concurrent append
        // of the same id waits here instead of racing past the check. (Microsoft.Data.Sqlite is synchronous
        // underneath; the async overload has no way to ask for an immediate transaction.)
        await using var transaction = connection.BeginTransaction(deferred: false);

        var existing = await ReadByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Idempotent by id: no second row, no watermark consumed.
            return existing;
        }

        var appendedAt = _clock.GetUtcNow();
        await using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            """
            INSERT INTO fb_raw_documents (id, form_type, body, appended_at) VALUES ($id, $type, $body, $at)
            RETURNING watermark
            """;
        insert.Parameters.AddWithValue("$id", id.Value.ToString("D"));
        insert.Parameters.AddWithValue("$type", type.Value);
        insert.Parameters.AddWithValue("$body", body.ToJsonString());
        insert.Parameters.AddWithValue("$at", Format(appendedAt));

        var watermark = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new StoredDocument(id, type, body, new Watermark(watermark), ReadInstant(Format(appendedAt)));
    }

    public async Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadByIdAsync(connection, transaction: null, id, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = $"{SelectColumns} WHERE form_type = $type AND watermark > $after ORDER BY watermark";
        command.Parameters.AddWithValue("$type", type.Value);
        command.Parameters.AddWithValue("$after", after.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadDocument(reader);
        }
    }

    public async Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(watermark), 0) FROM fb_raw_documents WHERE form_type = $type";
        command.Parameters.AddWithValue("$type", type.Value);

        var head = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new Watermark(head);
    }

    private static async Task<StoredDocument?> ReadByIdAsync(
        SqliteConnection connection, SqliteTransaction? transaction, DocumentId id, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"{SelectColumns} WHERE id = $id";
        command.Parameters.AddWithValue("$id", id.Value.ToString("D"));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDocument(reader) : null;
    }

    private static StoredDocument ReadDocument(SqliteDataReader reader) => new(
        new DocumentId(Guid.Parse(reader.GetString(0))),
        FormTypeRef.Create(reader.GetString(1)),
        DocumentBody.Parse(reader.GetString(2)),
        new Watermark(reader.GetInt64(3)),
        ReadInstant(reader.GetString(4)));

    private static string Format(DateTimeOffset instant) =>
        instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture);

    private static DateTimeOffset ReadInstant(string stored) =>
        DateTimeOffset.ParseExact(stored, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal);

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        await _database.EnsureAsync(Component, InitDdl, cancellationToken).ConfigureAwait(false);
        return await _database.OpenAsync(cancellationToken).ConfigureAwait(false);
    }
}
