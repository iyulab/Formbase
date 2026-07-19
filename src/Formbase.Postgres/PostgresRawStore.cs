using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
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
/// advisory lock keyed to the store's schema. This makes watermark <i>assignment order == commit order</i>:
/// without it, a sequence assigns watermark N to a transaction that commits <i>after</i> N+1, and a
/// projection running in that window would record a head that permanently skips the late-committing
/// document (silent data loss). The cost is that appends do not run concurrently within one store — an
/// acceptable trade for an append-only log of record at this stage. Reads (get/stream/head) are lock-free.</para>
/// </remarks>
public sealed partial class PostgresRawStore : IRawStore, IDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly string _schema;
    private readonly long _appendLockKey;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _initialized;

    /// <summary>
    /// Creates a store over <paramref name="dataSource"/> (whose lifetime the caller owns), isolated in
    /// <paramref name="schema"/>. The schema, its sequence, and its table are created on first use.
    /// </summary>
    public PostgresRawStore(NpgsqlDataSource dataSource, string schema = "formbase", TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(dataSource);
        ArgumentException.ThrowIfNullOrWhiteSpace(schema);
        if (!SchemaNamePattern().IsMatch(schema))
        {
            throw new ArgumentException($"Schema name '{schema}' is not a valid PostgreSQL identifier.", nameof(schema));
        }

        _dataSource = dataSource;
        _schema = schema;
        _appendLockKey = StableKey(schema);
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
            lockCommand.Parameters.AddWithValue("k", _appendLockKey);
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
            INSERT INTO "{_schema}".raw_documents (id, form_type, body, watermark, appended_at)
            VALUES (@id, @type, @body, nextval('"{_schema}".raw_watermark_seq'), @at)
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
            FROM "{_schema}".raw_documents
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
            $"""SELECT COALESCE(MAX(watermark), 0) FROM "{_schema}".raw_documents WHERE form_type = @type""",
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
            FROM "{_schema}".raw_documents WHERE id = @id
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

    private async ValueTask EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                $"""
                CREATE SCHEMA IF NOT EXISTS "{_schema}";
                CREATE SEQUENCE IF NOT EXISTS "{_schema}".raw_watermark_seq AS bigint START 1 MINVALUE 1;
                CREATE TABLE IF NOT EXISTS "{_schema}".raw_documents (
                    id uuid PRIMARY KEY,
                    form_type text NOT NULL,
                    body jsonb NOT NULL,
                    watermark bigint NOT NULL UNIQUE,
                    appended_at timestamptz NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_raw_documents_type_watermark
                    ON "{_schema}".raw_documents (form_type, watermark);
                """, connection);
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    /// <summary>A stable 64-bit key (FNV-1a) so each schema serializes its appends independently.</summary>
    private static long StableKey(string schema)
    {
        ulong hash = 14695981039346656037UL;
        foreach (var b in System.Text.Encoding.UTF8.GetBytes(schema))
        {
            hash = (hash ^ b) * 1099511628211UL;
        }

        return unchecked((long)hash);
    }

    [GeneratedRegex("^[a-zA-Z_][a-zA-Z0-9_]*$")]
    private static partial Regex SchemaNamePattern();

    /// <summary>Disposes the init gate. The injected data source is caller-owned and left untouched.</summary>
    public void Dispose() => _initGate.Dispose();
}
