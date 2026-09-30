using System.Runtime.CompilerServices;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Npgsql;
using NpgsqlTypes;

namespace Formbase.Postgres;

/// <summary>
/// The durable, append-only <see cref="IRawStore"/> — formbase's source of truth, backed directly by
/// PostgreSQL (never through MorphDB, so raw intake survives a projection-store outage). Watermarks are
/// a globally monotonic sequence, matching the in-memory reference store.
/// </summary>
/// <remarks>
/// <para><b>Concurrency guarantee.</b> Appends are serialized through a transaction-scoped Postgres
/// advisory lock keyed to the store's schema. The lock is <i>database-scoped</i>, so appends serialize
/// across every connection and process sharing the schema — not merely within one store instance; a
/// second in-process lock would be redundant. This makes watermark <i>assignment order == commit order</i>:
/// without it, a sequence assigns watermark N to a transaction that commits <i>after</i> N+1, and a
/// projection running in that window would record a head that permanently skips the late-committing
/// document (silent data loss). The cost is that appends do not run concurrently within one schema — an
/// acceptable trade for an append-only log of record at this stage. A batch (<see cref="AppendManyAsync"/>)
/// holds the lock for all of its appends, which is what keeps its watermarks consecutive. Reads
/// (get/stream/head) are lock-free.</para>
/// <para>Schema/table creation is likewise serialized under the same lock, because <c>CREATE … IF NOT
/// EXISTS</c> is not atomic against the catalog — two instances cold-starting against a fresh shared
/// schema could otherwise both create it and one would fail.</para>
/// <para><b>A schema from an earlier version is upgraded in place</b> under the same lock: the record-key
/// column is added and the body column accepts NULL, which is how a retirement is stored. A JSON
/// <c>null</c> document is <c>'null'::jsonb</c>, never SQL NULL, so the two cannot be confused. Documents
/// already stored read back as records of their own.</para>
/// </remarks>
public sealed class PostgresRawStore : IRawStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSchemaBootstrap _bootstrap;
    private readonly TimeProvider _clock;
    private readonly string _initDdl;

    /// <summary>
    /// Creates a store over <paramref name="dataSource"/> (whose lifetime the caller owns), isolated in
    /// <paramref name="schema"/>. The schema, its sequence, and its table are created on first use.
    /// </summary>
    public PostgresRawStore(NpgsqlDataSource dataSource, string schema = "formbase", TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);

        _dataSource = dataSource;
        _bootstrap = new PostgresSchemaBootstrap(dataSource, schema);
        _clock = clock ?? TimeProvider.System;
        _initDdl =
            $"""
            CREATE SCHEMA IF NOT EXISTS "{_bootstrap.Schema}";
            CREATE SEQUENCE IF NOT EXISTS "{_bootstrap.Schema}".raw_watermark_seq AS bigint START 1 MINVALUE 1;
            CREATE TABLE IF NOT EXISTS "{_bootstrap.Schema}".raw_documents (
                id uuid PRIMARY KEY,
                form_type text NOT NULL,
                body jsonb NULL,
                watermark bigint NOT NULL UNIQUE,
                appended_at timestamptz NOT NULL,
                record_key text NULL
            );
            DO $upgrade$
            BEGIN
                -- A table created by an earlier version. Checked first so an up-to-date schema takes no
                -- table lock at startup; ALTER TABLE locks even when it has nothing to change.
                IF NOT EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = '{_bootstrap.Schema}' AND table_name = 'raw_documents' AND column_name = 'record_key')
                THEN
                    ALTER TABLE "{_bootstrap.Schema}".raw_documents ADD COLUMN record_key text NULL;
                END IF;
                IF EXISTS (
                    SELECT 1 FROM information_schema.columns
                    WHERE table_schema = '{_bootstrap.Schema}' AND table_name = 'raw_documents' AND column_name = 'body'
                      AND is_nullable = 'NO')
                THEN
                    ALTER TABLE "{_bootstrap.Schema}".raw_documents ALTER COLUMN body DROP NOT NULL;
                END IF;
            END
            $upgrade$;
            CREATE INDEX IF NOT EXISTS ix_raw_documents_type_watermark
                ON "{_bootstrap.Schema}".raw_documents (form_type, watermark);
            """;
    }

    public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, RecordKey? key = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return AppendCoreAsync(type, RawAppend.Document(id, body, key), cancellationToken);
    }

    public Task<StoredDocument> RetireAsync(FormTypeRef type, DocumentId id, RecordKey key, CancellationToken cancellationToken = default)
        => AppendCoreAsync(type, RawAppend.Retirement(id, key), cancellationToken);

    private async Task<StoredDocument> AppendCoreAsync(FormTypeRef type, RawAppend append, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await BeginSerializedAsync(connection, cancellationToken).ConfigureAwait(false);

        var existing = await ReadByIdAsync(connection, transaction, append.Id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Idempotent by id: no second row, no watermark consumed.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        await using var insert = CreateInsert(connection, transaction);
        var stored = await InsertAsync(insert, type, append, _clock.GetUtcNow(), cancellationToken).ConfigureAwait(false);
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return stored;
    }

    public async Task<IReadOnlyList<StoredDocument>> AppendManyAsync(FormTypeRef type, IReadOnlyList<RawAppend> appends, CancellationToken cancellationToken = default)
    {
        var distinct = RawAppend.Distinct(type, appends);
        if (distinct.Count == 0)
        {
            return [];
        }

        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        // The lock is held for the whole batch, so the batch's watermarks are consecutive — and every
        // other append on the schema waits for it, as it would for that many appends in a row.
        await using var transaction = await BeginSerializedAsync(connection, cancellationToken).ConfigureAwait(false);

        // Every check before the first insert: a refused batch takes no watermark from the sequence,
        // which does not roll back.
        var stored = new Dictionary<DocumentId, StoredDocument>(distinct.Count);
        await using (var select = new NpgsqlCommand(
            $"""
            SELECT id, form_type, body, watermark, appended_at, record_key
            FROM "{_bootstrap.Schema}".raw_documents WHERE id = ANY(@ids)
            """, connection, transaction))
        {
            select.Parameters.AddWithValue("ids", distinct.Select(append => append.Id.Value).ToArray());
            await using var reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var held = ReadDocument(reader);
                stored.Add(held.Id, held);
            }
        }

        foreach (var append in distinct)
        {
            if (stored.TryGetValue(append.Id, out var held))
            {
                append.EnsureRepeats(type, held);
            }
        }

        var appendedAt = _clock.GetUtcNow();
        await using var insert = CreateInsert(connection, transaction);
        foreach (var append in distinct)
        {
            if (!stored.ContainsKey(append.Id))
            {
                stored.Add(append.Id, await InsertAsync(insert, type, append, appendedAt, cancellationToken).ConfigureAwait(false));
            }
        }

        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        return appends.Select(append => stored[append.Id]).ToArray();
    }

    /// <summary>
    /// Opens a transaction holding the schema-scoped advisory lock through commit, so watermark assignment
    /// order equals commit order. Also closes the check-then-insert race for a duplicate id.
    /// </summary>
    private async Task<NpgsqlTransaction> BeginSerializedAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", connection, transaction);
            lockCommand.Parameters.AddWithValue("k", _bootstrap.AdvisoryLockKey);
            await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return transaction;
        }
        catch
        {
            await transaction.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private NpgsqlCommand CreateInsert(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        var insert = new NpgsqlCommand(
            $"""
            INSERT INTO "{_bootstrap.Schema}".raw_documents (id, form_type, body, watermark, appended_at, record_key)
            VALUES (@id, @type, @body, nextval('"{_bootstrap.Schema}".raw_watermark_seq'), @at, @key)
            RETURNING watermark
            """, connection, transaction);
        insert.Parameters.Add(new NpgsqlParameter("id", NpgsqlDbType.Uuid));
        insert.Parameters.Add(new NpgsqlParameter("type", NpgsqlDbType.Text));
        insert.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb));
        insert.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz));
        insert.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Text));
        return insert;
    }

    private static async Task<StoredDocument> InsertAsync(
        NpgsqlCommand insert, FormTypeRef type, RawAppend append, DateTimeOffset appendedAt, CancellationToken cancellationToken)
    {
        insert.Parameters["id"].Value = append.Id.Value;
        insert.Parameters["type"].Value = type.Value;
        insert.Parameters["body"].Value = (object?)append.Body?.ToJsonString() ?? DBNull.Value;
        insert.Parameters["at"].Value = appendedAt;
        insert.Parameters["key"].Value = (object?)append.Key?.Value ?? DBNull.Value;

        var watermark = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new StoredDocument(append.Id, type, append.Body, new Watermark(watermark), appendedAt, append.Key);
    }

    public async Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadByIdAsync(connection, transaction: null, id, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, form_type, body, watermark, appended_at, record_key
            FROM "{_bootstrap.Schema}".raw_documents
            WHERE form_type = @type AND watermark > @after
            ORDER BY watermark
            """, connection);
        command.Parameters.AddWithValue("type", type.Value);
        command.Parameters.AddWithValue("after", after.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return ReadDocument(reader);
        }
    }

    public async Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""SELECT COALESCE(MAX(watermark), 0) FROM "{_bootstrap.Schema}".raw_documents WHERE form_type = @type""",
            connection);
        command.Parameters.AddWithValue("type", type.Value);

        var head = (long)(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new Watermark(head);
    }

    private async Task<StoredDocument?> ReadByIdAsync(NpgsqlConnection connection, NpgsqlTransaction? transaction, DocumentId id, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            SELECT id, form_type, body, watermark, appended_at, record_key
            FROM "{_bootstrap.Schema}".raw_documents WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDocument(reader) : null;
    }

    private static StoredDocument ReadDocument(NpgsqlDataReader reader) => new(
        new DocumentId(reader.GetGuid(0)),
        FormTypeRef.Create(reader.GetString(1)),
        reader.IsDBNull(2) ? null : DocumentBody.Parse(reader.GetString(2)),
        new Watermark(reader.GetInt64(3)),
        reader.GetFieldValue<DateTimeOffset>(4),
        reader.IsDBNull(5) ? null : RecordKey.Create(reader.GetString(5)));

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
        => _bootstrap.EnsureAsync(_initDdl, cancellationToken);

    /// <summary>Disposes the init gate. The injected data source is caller-owned and left untouched.</summary>
    public void Dispose() => _bootstrap.Dispose();
}
