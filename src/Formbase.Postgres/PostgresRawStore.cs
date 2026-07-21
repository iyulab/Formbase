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
/// acceptable trade for an append-only log of record at this stage. Reads (get/stream/head) are lock-free.</para>
/// <para>Schema/table creation is likewise serialized under the same lock, because <c>CREATE … IF NOT
/// EXISTS</c> is not atomic against the catalog — two instances cold-starting against a fresh shared
/// schema could otherwise both create it and one would fail.</para>
/// </remarks>
public sealed class PostgresRawStore : IRawStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresSchemaBootstrap _bootstrap;
    private readonly TimeProvider _clock;

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
    }

    public async Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await EnsureInitializedAsync(cancellationToken).ConfigureAwait(false);

        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        // Serialize appends: hold a schema-scoped advisory lock through commit so watermark assignment
        // order equals commit order. Also closes the check-then-insert race for a duplicate id.
        await using (var lockCommand = new NpgsqlCommand("SELECT pg_advisory_xact_lock(@k)", connection, transaction))
        {
            lockCommand.Parameters.AddWithValue("k", _bootstrap.AdvisoryLockKey);
            await lockCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        }

        var existing = await ReadByIdAsync(connection, transaction, id, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Idempotent by id: no second row, no watermark consumed.
            await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return existing;
        }

        var appendedAt = _clock.GetUtcNow();
        await using var insert = new NpgsqlCommand(
            $"""
            INSERT INTO "{_bootstrap.Schema}".raw_documents (id, form_type, body, watermark, appended_at)
            VALUES (@id, @type, @body, nextval('"{_bootstrap.Schema}".raw_watermark_seq'), @at)
            RETURNING watermark
            """, connection, transaction);
        insert.Parameters.AddWithValue("id", id.Value);
        insert.Parameters.AddWithValue("type", type.Value);
        insert.Parameters.Add(new NpgsqlParameter("body", NpgsqlDbType.Jsonb) { Value = body.ToJsonString() });
        insert.Parameters.Add(new NpgsqlParameter("at", NpgsqlDbType.TimestampTz) { Value = appendedAt });

        var watermark = (long)(await insert.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        return new StoredDocument(id, type, body, new Watermark(watermark), appendedAt);
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
            SELECT id, form_type, body, watermark, appended_at
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
            SELECT id, form_type, body, watermark, appended_at
            FROM "{_bootstrap.Schema}".raw_documents WHERE id = @id
            """, connection, transaction);
        command.Parameters.AddWithValue("id", id.Value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? ReadDocument(reader) : null;
    }

    private static StoredDocument ReadDocument(NpgsqlDataReader reader) => new(
        new DocumentId(reader.GetGuid(0)),
        FormTypeRef.Create(reader.GetString(1)),
        DocumentBody.Parse(reader.GetString(2)),
        new Watermark(reader.GetInt64(3)),
        reader.GetFieldValue<DateTimeOffset>(4));

    private ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
        => _bootstrap.EnsureAsync(
            $"""
            CREATE SCHEMA IF NOT EXISTS "{_bootstrap.Schema}";
            CREATE SEQUENCE IF NOT EXISTS "{_bootstrap.Schema}".raw_watermark_seq AS bigint START 1 MINVALUE 1;
            CREATE TABLE IF NOT EXISTS "{_bootstrap.Schema}".raw_documents (
                id uuid PRIMARY KEY,
                form_type text NOT NULL,
                body jsonb NOT NULL,
                watermark bigint NOT NULL UNIQUE,
                appended_at timestamptz NOT NULL
            );
            CREATE INDEX IF NOT EXISTS ix_raw_documents_type_watermark
                ON "{_bootstrap.Schema}".raw_documents (form_type, watermark);
            """,
            cancellationToken);

    /// <summary>Disposes the init gate. The injected data source is caller-owned and left untouched.</summary>
    public void Dispose() => _bootstrap.Dispose();
}
