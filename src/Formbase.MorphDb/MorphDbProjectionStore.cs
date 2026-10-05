using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using System.Globalization;
using MorphDB.Client;
using MorphDB.Client.Models;
using FormbaseOperator = Formbase.Core.Query.FilterOperator;
using MorphOperator = MorphDB.Client.Models.FilterOperator;

namespace Formbase.MorphDb;

/// <summary>
/// <see cref="IProjectionStore"/> implemented over MorphDB's REST client. A thin translation layer:
/// formbase schema/rows/filters in, MorphDB API requests out, MorphDB records back. Holds no policy
/// of its own — the drop-and-rebuild orchestration lives in the core projector.
/// </summary>
public sealed class MorphDbProjectionStore : IProjectionStore
{
    private const int InsertChunkSize = 500;
    private const int DefaultPageSize = 50;

    /// <summary>MorphDB returns at most this many rows per request, so a wider window takes several.</summary>
    private const int MaxPageSize = 1000;

    private readonly MorphDBClient _client;

    public MorphDbProjectionStore(MorphDBClient client) => _client = client;

    public async Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default)
        => await _client.Schema.GetTableAsync(tableName, cancellationToken).ConfigureAwait(false) is not null;

    public async Task DropTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        try
        {
            await _client.Schema.DropTableAsync(tableName, cancellationToken).ConfigureAwait(false);
        }
        catch (MorphDBNotFoundException)
        {
            // Already absent — drop is idempotent.
        }
    }

    public async Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default)
    {
        // Only the generic column shape crosses into MorphDB — projected tables are generic by
        // design (FormType never reaches MorphDB). The declaration axes stay formbase-internal:
        // SourceKey is an extraction concern (the projected column is just Name), and Binding is
        // declaration semantics MorphDB has no notion of.
        var request = new CreateTableRequest
        {
            Name = schema.TableName,
            Columns = schema.Columns
                .Select(c => new CreateColumnRequest
                {
                    Name = c.Name,
                    Type = MorphDbTypeMap.ToMorphType(c.Type),
                    Nullable = c.Nullable,
                })
                .ToList(),
        };

        await _client.Schema.CreateTableAsync(request, cancellationToken).ConfigureAwait(false);

        if (schema.Relations is { Count: > 0 })
        {
            foreach (var relation in schema.Relations)
            {
                await MaterializeRelationAsync(schema.TableName, relation, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Materializes one declared relation as a MorphDB virtual FK, always with
    /// <c>EnforceOnWrite: false</c> — the drop-and-rebuild orchestration in the core projector gives
    /// no ordering guarantee between a form type's table and the tables its relations name, so a
    /// child can (and, on the first projection of either side, will) be written before its parent
    /// has been reloaded. Enforcing would reject data that is consistent at its source; this project
    /// analysis settled on non-enforcing metadata for exactly that reason. No
    /// physical constraint follows either, which is what keeps a later drop of either table free of
    /// MorphDB's <c>TABLE_HAS_DEPENDENTS</c> refusal.
    /// <para>
    /// Only <see cref="RelationKind.Child"/> is materialized. <see cref="RelationKind.Reference"/>'s
    /// key field names a column on *this* table, but nothing in the declaration vocabulary states
    /// which column on the target it is matched against — <see cref="RelationKind.Child"/> has a
    /// real answer (the same field name declared on both sides, the only reading consistent with how
    /// <see cref="Formbase.Core.Projection.HintSchemaProposer"/> resolves <c>FieldBinding.Reference</c>
    /// targets, and the one actual fixture — <c>EuMultiLotProcurementNoticeRegressionTests</c> —
    /// exercises) but Reference does not, so a source-side FK column still projects as a normal
    /// column and the relation stays undeclared to MorphDB rather than materialized on a guess.
    /// </para>
    /// <para>
    /// Whichever side of the relation is not the table just created may not exist yet — the first
    /// projection of a parent whose relation names a not-yet-projected child is expected, not an
    /// error. MorphDB answers that case as <c>400 TABLE_NOT_FOUND</c> (a validation response, not a
    /// 404 — the requested resource is the relation, not the table), which this catches by error
    /// code and skips: the relation reappears on this table's next rebuild, once the other side
    /// exists. Anything else — a real validation failure, a naming collision — propagates, the same
    /// as <see cref="Formbase.Core.Projection.ProjectionResult.UnresolvedReferences"/> reports an
    /// unresolved <c>FieldBinding.Reference</c> by name rather than swallowing it.
    /// </para>
    /// </summary>
    private async Task MaterializeRelationAsync(string tableName, RelationDef relation, CancellationToken cancellationToken)
    {
        if (relation.Kind != RelationKind.Child)
        {
            return;
        }

        var request = new CreateRelationRequest
        {
            Name = relation.Name,
            SourceTable = relation.TargetTable,
            SourceColumn = relation.KeyColumn,
            TargetTable = tableName,
            TargetColumn = relation.KeyColumn,
            EnforceOnWrite = false,
        };

        try
        {
            await _client.Schema.CreateRelationAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (MorphDBValidationException ex) when (ex.ErrorCode == "TABLE_NOT_FOUND")
        {
            // The child table this relation names has not been projected yet — its own projection
            // will materialize the same relation once this (parent) table exists, which it now does.
        }
    }

    public async Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var inserted = 0;
        foreach (var chunk in rows.Chunk(InsertChunkSize))
        {
            var records = chunk
                .Select(r => (IDictionary<string, object?>)new Dictionary<string, object?>(r, StringComparer.Ordinal))
                .ToList();

            var response = await _client.Batch.InsertManyAsync(tableName, records, cancellationToken).ConfigureAwait(false);

            // A batch reports per-operation outcomes and stays a 200 even when some rows fail, so a
            // partial failure has to be raised here rather than silently shrinking the count.
            if (response.FailureCount > 0)
            {
                var reason = response.Results.FirstOrDefault(r => !r.Success)?.Error ?? "unknown";
                throw new InvalidOperationException(
                    $"MorphDB rejected {response.FailureCount} of {records.Count} rows for '{tableName}': {reason}");
            }

            inserted += response.SuccessCount;
        }

        return inserted;
    }

    /// <summary>
    /// MorphDB deletes by record id only, so the rows to remove are found first — one equality query per
    /// key or document, which suits the few records a projection brings forward at a time — and deleted
    /// in one batch, then the new rows inserted. Neither step spans the other in a transaction; a retry
    /// after a failure between them is safe because the call is idempotent.
    /// </summary>
    public async Task<int> ReplaceRowsAsync(
        string tableName,
        IReadOnlyCollection<RecordKey> removeKeys,
        IReadOnlyCollection<DocumentId> removeDocuments,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(removeKeys);
        ArgumentNullException.ThrowIfNull(removeDocuments);
        ArgumentNullException.ThrowIfNull(rows);

        var ids = new HashSet<Guid>();
        var matches = removeKeys.Select(k => (Column: ProjectionSystemColumns.RecordKey, Value: (object)k.Value))
            .Concat(removeDocuments.Select(d => (Column: ProjectionSystemColumns.DocumentId, Value: (object)d.Value)));
        foreach (var (column, value) in matches)
        {
            for (var page = 1; ; page++)
            {
                var request = new QueryRequest { Filters = [new Filter(column, MorphOperator.Equal, value)], PageSize = MaxPageSize, Page = page };
                var paged = await _client.Data.QueryAsync(tableName, request, cancellationToken).ConfigureAwait(false);
                ids.UnionWith(paged.Data.Select(record => record.Id));
                if (paged.Data.Count < MaxPageSize)
                {
                    break;
                }
            }
        }

        foreach (var chunk in ids.Chunk(InsertChunkSize))
        {
            var response = await _client.Batch.ExecuteAsync(
                new BatchRequest { Operations = [.. chunk.Select(id => new BatchOperation { Method = BatchMethod.Delete, Table = tableName, Id = id })] },
                cancellationToken).ConfigureAwait(false);
            if (response.FailureCount > 0)
            {
                var reason = response.Results.FirstOrDefault(r => !r.Success)?.Error ?? "unknown";
                throw new InvalidOperationException(
                    $"MorphDB refused to delete {response.FailureCount} of {chunk.Length} rows from '{tableName}': {reason}");
            }
        }

        return await BulkInsertAsync(tableName, rows, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        var limit = spec.Limit ?? DefaultPageSize;
        var offset = spec.Offset ?? 0;

        if (limit <= 0)
        {
            return [];
        }

        var filters = (spec.Filters ?? []).Select(f => new Filter(f.Column, ToMorph(f), f.Value)).ToList();

        // Server-side ordering — the only way paging is deterministic (a client-side sort would order
        // an already-arbitrary page). Descending maps to ascending: false.
        var orderBy = spec.OrderBy is { Count: > 0 } specOrder
            ? specOrder.Select(k => new OrderBy(k.Column, ascending: !k.Descending)).ToList()
            : [];

        // The window is read from where it starts: one request, unless it is wider than the most rows
        // MorphDB returns at once, in which case each further request starts where the last one ended.
        var window = new List<IReadOnlyDictionary<string, object?>>(Math.Min(limit, MaxPageSize));
        var position = offset;
        while (window.Count < limit)
        {
            var take = Math.Min(limit - window.Count, MaxPageSize);
            var request = new QueryRequest
            {
                Filters = filters,
                OrderBy = orderBy,
                PageSize = take,
                Offset = position,
            };

            var paged = await _client.Data
                .QueryAsync(tableName, request, cancellationToken)
                .ConfigureAwait(false);

            window.AddRange(paged.Data
                .Select(record => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(record.Data, StringComparer.Ordinal)));

            // Fewer rows than asked for means the table ran out — asking again would only read nothing.
            if (paged.Data.Count < take)
            {
                break;
            }

            position += take;
        }

        return window;
    }

    public async Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default)
    {
        var groupBy = spec.GroupBy ?? [];
        // The records behind each group ride the same request as its count: two ARRAY_AGGs — the document
        // ids and the record keys — both in the order the documents were accepted and cut at the same
        // limit by the server. ARRAY_AGG keeps nulls, so a document that is a record of its own keeps its
        // place in the keys and the two arrays pair up index by index.
        var request = new AggregationRequest
        {
            Aggregations = spec.RecordsPerGroup is { } limit
                ?
                [
                    AggregationColumn.Count(CountAlias),
                    AggregationColumn.ArrayAgg(ProjectionSystemColumns.DocumentId, DocumentsAlias, limit, orderBy: ProjectionSystemColumns.Watermark),
                    AggregationColumn.ArrayAgg(ProjectionSystemColumns.RecordKey, KeysAlias, limit, orderBy: ProjectionSystemColumns.Watermark),
                ]
                : [AggregationColumn.Count(CountAlias)],
            GroupBy = groupBy,
            Filter = (spec.Filters ?? []).Select(f => new AggregationFilter(f.Column, ToMorph(f), f.Value)).ToList(),
        };

        var response = await _client.Data.AggregateAsync(tableName, request, cancellationToken).ConfigureAwait(false);

        return response.Data
            .Select(row => new AggregateGroup(
                groupBy.ToDictionary(column => column, column => row.TryGetValue(column, out var value) ? value : null, StringComparer.Ordinal),
                Convert.ToInt64(row[CountAlias], CultureInfo.InvariantCulture),
                spec.RecordsPerGroup is null
                    ? null
                    : Records(row.TryGetValue(DocumentsAlias, out var documents) ? documents : null, row.TryGetValue(KeysAlias, out var keys) ? keys : null)))
            .ToList();
    }

    /// <summary>The document ids' alias; prefixed like <see cref="CountAlias"/> so no grouping column shares it.</summary>
    private const string DocumentsAlias = "fb_documents";

    /// <summary>The record keys' alias; prefixed like <see cref="CountAlias"/> so no grouping column shares it.</summary>
    private const string KeysAlias = "fb_record_keys";

    /// <summary>
    /// Pairs the two arrays index by index. Arrays of different lengths mean the server did not keep a
    /// null key in its place — pairing them anyway would name the wrong record for a document, so the
    /// answer is refused instead.
    /// </summary>
    private static List<RecordRef> Records(object? documents, object? keys)
    {
        var ids = Elements(documents);
        var names = Elements(keys);
        if (ids.Count != names.Count)
        {
            throw new InvalidOperationException(
                $"MorphDB answered {ids.Count} documents but {names.Count} record keys for one group; the two are read in the same order and must pair up.");
        }

        return ids
            .Select((id, i) => new RecordRef(
                DocumentId.From(id is Guid guid ? guid : Guid.Parse(Convert.ToString(id, CultureInfo.InvariantCulture)!)),
                names[i] is { } key ? RecordKey.Create(Convert.ToString(key, CultureInfo.InvariantCulture)!) : null))
            .ToList();
    }

    private static List<object?> Elements(object? array) =>
        array is System.Collections.IEnumerable values and not string ? values.Cast<object?>().ToList() : [];

    /// <summary>
    /// The alias the count comes back under. The projection's bookkeeping prefix keeps it clear of every
    /// declared column, which a grouping column in the same row could otherwise share a name with.
    /// </summary>
    private const string CountAlias = "fb_count";

    /// <summary>Each formbase operator is one MorphDB answers natively — a translation, not a policy.</summary>
    /// <summary>
    /// MorphDB's operator for a filter. Equality with null asks whether the column is empty, so it goes as
    /// <c>isnull</c>: sent as <c>eq</c>, the missing value would reach the server as an empty string and
    /// compare against that instead.
    /// </summary>
    private static MorphOperator ToMorph(FieldFilter filter) => filter switch
    {
        { Operator: FormbaseOperator.Equal, Value: null } => MorphOperator.IsNull,
        _ => ToMorph(filter.Operator),
    };

    private static MorphOperator ToMorph(FormbaseOperator op) => op switch
    {
        FormbaseOperator.Equal => MorphOperator.Equal,
        FormbaseOperator.IsNull => MorphOperator.IsNull,
        FormbaseOperator.IsNotNull => MorphOperator.IsNotNull,
        FormbaseOperator.GreaterThan => MorphOperator.GreaterThan,
        FormbaseOperator.GreaterThanOrEqual => MorphOperator.GreaterThanOrEqual,
        FormbaseOperator.LessThan => MorphOperator.LessThan,
        FormbaseOperator.LessThanOrEqual => MorphOperator.LessThanOrEqual,
        FormbaseOperator.Contains => MorphOperator.Contains,
        FormbaseOperator.StartsWith => MorphOperator.StartsWith,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "No MorphDB operator for this filter."),
    };
}
