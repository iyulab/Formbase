using Formbase.Core.Errors;
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

    /// <summary>The metadata key a table's owner id is stored under.</summary>
    public const string OwnerKey = "formbase.owner";

    /// <summary>The metadata key the owner's readable description is stored under.</summary>
    public const string OwnerLabelKey = "formbase.owner-label";

    private readonly MorphDBClient _client;
    private readonly IProjectionTableOwnerSource? _owner;

    /// <summary>
    /// Creates a store over <paramref name="client"/>. With an <paramref name="owner"/>, every table the
    /// store creates carries the owner's mark, and a table carrying another owner's mark is refused
    /// rather than dropped (<see cref="ProjectionTableOwnedElsewhereException"/>). Without one, tables
    /// are unmarked and any table of the requested name is replaced — right only when nothing else
    /// writes into the MorphDB project.
    /// </summary>
    public MorphDbProjectionStore(MorphDBClient client, IProjectionTableOwnerSource? owner = null)
    {
        _client = client;
        _owner = owner;
    }

    // Every port call goes through one translation: a MorphDB project deleted after the host started
    // answers 404 PROJECT_NOT_FOUND to everything, which read as "no such table" (and a drop of an
    // absent table) until the create failed with an error naming neither.

    public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) =>
        Translated(() => TableExistsCoreAsync(tableName, cancellationToken));

    public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) =>
        Translated(async () => { await DropTableCoreAsync(tableName, cancellationToken).ConfigureAwait(false); return true; });

    public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) =>
        Translated(async () => { await CreateTableCoreAsync(schema, cancellationToken).ConfigureAwait(false); return true; });

    public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) =>
        Translated(() => BulkInsertCoreAsync(tableName, rows, cancellationToken));

    public Task<int> ReplaceRowsAsync(
        string tableName,
        IReadOnlyCollection<RecordKey> removeKeys,
        IReadOnlyCollection<DocumentId> removeDocuments,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default) =>
        Translated(() => ReplaceRowsCoreAsync(tableName, removeKeys, removeDocuments, rows, cancellationToken));

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default) =>
        Translated(() => QueryCoreAsync(tableName, spec, cancellationToken));

    public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default) =>
        Translated(() => AggregateCoreAsync(tableName, spec, cancellationToken));

    private static async Task<T> Translated<T>(Func<Task<T>> call)
    {
        try
        {
            return await call().ConfigureAwait(false);
        }
        catch (MorphDBNotFoundException missing) when (IsMissingProject(missing))
        {
            throw new ProjectionStoreMissingException(
                "The MorphDB project this host projects into no longer exists (PROJECT_NOT_FOUND) — it was " +
                "deleted after the host started. Create a project, set Formbase:MorphDb:ProjectId to its id and " +
                "restart; the projections are rebuilt from the raw stream, which is unaffected.",
                missing);
        }
    }

    private static bool IsMissingProject(MorphDBNotFoundException exception) =>
        string.Equals(exception.ErrorCode, "PROJECT_NOT_FOUND", StringComparison.Ordinal);

    private async Task<bool> TableExistsCoreAsync(string tableName, CancellationToken cancellationToken = default)
        => await _client.Schema.GetTableAsync(tableName, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Drops the table unless another owner built it. A table with no mark — built before marks
    /// existed, or by a store with no owner — is dropped as before; the rebuild that follows marks it.
    /// The check and the drop are two requests, so two owners racing for one unmarked table can still
    /// both pass it; the mark makes every later collision visible.
    /// </summary>
    private async Task DropTableCoreAsync(string tableName, CancellationToken cancellationToken = default)
    {
        if (_owner is not null)
        {
            var owner = await _owner.GetOwnerAsync(cancellationToken).ConfigureAwait(false);
            var existing = await _client.Schema.GetTableAsync(tableName, cancellationToken).ConfigureAwait(false);
            if (existing is null)
            {
                return;
            }

            if (existing.Metadata.TryGetValue(OwnerKey, out var holder)
                && !string.Equals(holder, owner.Id, StringComparison.Ordinal))
            {
                var holderLabel = existing.Metadata.TryGetValue(OwnerLabelKey, out var label) && label.Length > 0
                    ? $"{label} (owner '{holder}')"
                    : $"owner '{holder}'";
                throw new ProjectionTableOwnedElsewhereException(tableName, holderLabel, $"{owner.Label} (owner '{owner.Id}')");
            }
        }

        try
        {
            await _client.Schema.DropTableAsync(tableName, cancellationToken).ConfigureAwait(false);
        }
        catch (MorphDBNotFoundException missing) when (!IsMissingProject(missing))
        {
            // Already absent — drop is idempotent.
        }
    }

    private async Task CreateTableCoreAsync(TableSchema schema, CancellationToken cancellationToken = default)
    {
        // Only the generic column shape crosses into MorphDB — projected tables are generic by
        // design (FormType never reaches MorphDB). The declaration axes stay formbase-internal:
        // SourceKey is an extraction concern (the projected column is just Name), and Binding is
        // declaration semantics MorphDB has no notion of. A reference is the exception that proves it:
        // what crosses is not the binding but how to compute the column, as a lookup.
        var request = new CreateTableRequest
        {
            Name = schema.TableName,
            Columns = schema.Columns
                .SelectMany(c => c.Reference is { } reference
                    ? [Column(c.Name, c.Type, c.Nullable, Lookup(reference, reference.ValueColumn)), .. SourceColumns(c.Name, reference)]
                    : new[] { Column(c.Name, c.Type, c.Nullable, lookup: null) })
                .ToList(),
            Metadata = _owner is null ? null : OwnerMark(await _owner.GetOwnerAsync(cancellationToken).ConfigureAwait(false)),
        };

        await _client.Schema.CreateTableAsync(request, cancellationToken).ConfigureAwait(false);

        // Declared indexes, numbered in declared order: MorphDB keys an index's physical name by its
        // table, so the number only has to be unique here, and a redeclared set rebuilds the table.
        var indexes = schema.Indexes ?? [];
        for (var ordinal = 0; ordinal < indexes.Count; ordinal++)
        {
            await _client.Schema.CreateIndexAsync(
                schema.TableName,
                new CreateIndexRequest { Name = $"fb_dx_{ordinal}", Columns = [.. indexes[ordinal].Columns] },
                cancellationToken).ConfigureAwait(false);
        }

        if (schema.Relations is { Count: > 0 })
        {
            foreach (var relation in schema.Relations)
            {
                await MaterializeRelationAsync(schema.TableName, relation, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static Dictionary<string, string> OwnerMark(ProjectionTableOwner owner) => new(StringComparer.Ordinal)
    {
        [OwnerKey] = owner.Id,
        [OwnerLabelKey] = owner.Label,
    };

    /// <summary>
    /// A reference computed by MorphDB when it is read: the target row whose matched column equals the
    /// via column's value — the lookup field, or the record identity every projected row carries — the
    /// latest accepted when several match, null when none does.
    /// <para>
    /// <c>whenTargetMissing: null</c> because the target is another projection with a life of its own:
    /// projections run in no particular order and rebuild by dropping and recreating their table, so the
    /// target may not exist yet, may be mid-rebuild, or may not yet carry the column a redeclaration
    /// names. Each of those reads as no match — and the result as stale, which the core decides from
    /// the target's projection state — rather than refusing this table or failing its reads.
    /// </para>
    /// </summary>
    private static LookupConfig Lookup(ReferenceDef reference, string targetColumn) => new()
    {
        RelationColumn = reference.ViaColumn,
        TargetTable = reference.TargetTable,
        TargetColumn = targetColumn,
        MatchColumn = reference.Lookup is TargetLookup.FieldLookup field ? field.Name : ProjectionSystemColumns.Record,
        // A section's rows share their parent's watermark; among them the first by position is read.
        // Only for a section: ordering by a column the target lacks reads as a target that cannot answer.
        OrderBy = reference.TargetIsSection
            ? $"{ProjectionSystemColumns.Watermark} desc, {ProjectionSystemColumns.Row} asc"
            : $"{ProjectionSystemColumns.Watermark} desc",
        WhenTargetMissing = "null",
    };

    private static CreateColumnRequest Column(string name, ColumnType type, bool nullable, LookupConfig? lookup) => new()
    {
        Name = name,
        Type = MorphDbTypeMap.ToMorphType(type),
        Nullable = nullable,
        Lookup = lookup,
    };

    /// <summary>
    /// Where a reference's value is read from, as two more lookups of the same target row — same
    /// match, same order, so the row whose value is read is the row whose document and key are — under
    /// the names a query asking for sources reads (<see cref="QuerySpec.ReferenceSources"/>). A lookup
    /// returns one column, so the record's two halves take one each.
    /// </summary>
    private static IEnumerable<CreateColumnRequest> SourceColumns(string column, ReferenceDef reference) =>
    [
        Column(ProjectionSystemColumns.SourceDocumentOf(column), ColumnType.Uuid, nullable: true, Lookup(reference, ProjectionSystemColumns.DocumentId)),
        Column(ProjectionSystemColumns.SourceKeyOf(column), ColumnType.Text, nullable: true, Lookup(reference, ProjectionSystemColumns.RecordKey)),
        .. reference.TargetIsSection
            ? new[] { Column(ProjectionSystemColumns.SourceRowOf(column), ColumnType.Integer, nullable: true, Lookup(reference, ProjectionSystemColumns.Row)) }
            : [],
    ];

    /// <summary>
    /// The columns a query that did not ask for sources reads: every column but the source lookups.
    /// MorphDB computes a lookup only where the statement uses it, and a read that names no columns
    /// uses them all — two correlated lookups per reference column on every row, for values the shaped
    /// row then drops. A table without source lookups is read whole (an empty list asks for every
    /// column), as is one that is not there, which the read itself then reports.
    /// </summary>
    private static List<string> WithoutSources(TableInfo? table)
    {
        var columns = table?.Columns ?? [];
        var sources = columns
            .Where(c => c.Lookup is not null)
            .SelectMany(c => new[] { ProjectionSystemColumns.SourceDocumentOf(c.Name), ProjectionSystemColumns.SourceKeyOf(c.Name), ProjectionSystemColumns.SourceRowOf(c.Name) })
            .ToHashSet(StringComparer.Ordinal);
        return columns.Any(c => sources.Contains(c.Name))
            ? columns.Select(c => c.Name).Where(name => !sources.Contains(name)).ToList()
            : [];
    }

    /// <summary>
    /// Whether each reference's target can answer, by reference column — what the lookups cannot say,
    /// since a lookup into a target that is missing, or lacks a column, reads null as a lookup that
    /// matched nothing does. Asked of the target's table as it stands when the query is read. A table
    /// built before it carried the source lookups has no entry: its sources are not known.
    /// </summary>
    private async Task<Dictionary<string, bool>> AnswersAsync(TableInfo? table, CancellationToken cancellationToken)
    {
        var columns = table?.Columns ?? [];
        var targets = new Dictionary<string, TableInfo?>(StringComparer.Ordinal);
        var answers = new Dictionary<string, bool>(StringComparer.Ordinal);
        foreach (var value in columns.Where(c => c.Lookup is not null))
        {
            if (!columns.Any(c => c.Name == ProjectionSystemColumns.SourceDocumentOf(value.Name)))
            {
                continue;
            }

            var lookup = value.Lookup!;
            if (!targets.TryGetValue(lookup.TargetTable, out var target))
            {
                target = await _client.Schema.GetTableAsync(lookup.TargetTable, cancellationToken).ConfigureAwait(false);
                targets[lookup.TargetTable] = target;
            }

            answers[value.Name] = target is not null
                && target.Columns.Any(c => c.Name == lookup.TargetColumn)
                && target.Columns.Any(c => c.Name == lookup.MatchColumn);
        }

        return answers;
    }

    /// <summary>
    /// The rows as MorphDB takes them: without the columns it computes. A projected row carries every
    /// declared column, a reference's empty, and MorphDB refuses a value for a column it computes.
    /// </summary>
    private async Task<List<IDictionary<string, object?>>> WritableAsync(
        string tableName, IEnumerable<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken)
    {
        var table = await _client.Schema.GetTableAsync(tableName, cancellationToken).ConfigureAwait(false);
        var computed = (table?.Columns ?? []).Where(c => c.IsDerived).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        return rows
            .Select(r => (IDictionary<string, object?>)r
                .Where(field => !computed.Contains(field.Key))
                .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal))
            .ToList();
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

    private async Task<int> BulkInsertCoreAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        if (rows.Count == 0)
        {
            return 0;
        }

        var inserted = 0;
        foreach (var chunk in (await WritableAsync(tableName, rows, cancellationToken).ConfigureAwait(false)).Chunk(InsertChunkSize))
        {
            var records = chunk.ToList();

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
    private async Task<int> ReplaceRowsCoreAsync(
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
                if (!paged.Pagination.HasNext)
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

    private async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryCoreAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
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

        // One read of the table's shape serves both: which columns to ask for, and — when sources are
        // asked — whether each reference's target can answer.
        var table = await _client.Schema.GetTableAsync(tableName, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<string> select = spec.ReferenceSources ? [] : WithoutSources(table);
        var answers = spec.ReferenceSources
            ? await AnswersAsync(table, cancellationToken).ConfigureAwait(false)
            : null;

        // The window is read from where it starts: one request, unless it is wider than the most rows
        // MorphDB returns at once, in which case each further request starts where the last one ended.
        var window = new List<IReadOnlyDictionary<string, object?>>(Math.Min(limit, MaxPageSize));
        var position = offset;
        while (window.Count < limit)
        {
            var take = Math.Min(limit - window.Count, MaxPageSize);
            var request = new QueryRequest
            {
                Select = select,
                Filters = filters,
                OrderBy = orderBy,
                PageSize = take,
                Offset = position,
            };

            var paged = await _client.Data
                .QueryAsync(tableName, request, cancellationToken)
                .ConfigureAwait(false);

            window.AddRange(paged.Data.Select(record =>
            {
                var row = new Dictionary<string, object?>(record.Data, StringComparer.Ordinal);
                foreach (var (column, answer) in answers ?? [])
                {
                    row[ProjectionSystemColumns.SourceAnswersOf(column)] = answer;
                }

                return (IReadOnlyDictionary<string, object?>)row;
            }));

            // The server says whether rows follow, so a page that ends the table is the last request —
            // also when it is exactly full.
            if (!paged.Pagination.HasNext)
            {
                break;
            }

            position += take;
        }

        return window;
    }

    private async Task<IReadOnlyList<AggregateGroup>> AggregateCoreAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default)
    {
        var groupBy = spec.GroupBy ?? [];
        var measures = spec.Measures ?? [];
        // The records behind each group ride the same request as its count: two ARRAY_AGGs — the document
        // ids and the record keys — both in the order the documents were accepted and cut at the same
        // limit by the server. ARRAY_AGG keeps nulls, so a document that is a record of its own keeps its
        // place in the keys and the two arrays pair up index by index.
        // Each measure rides it too, as its value and COUNT(column) — the rows that had one.
        List<AggregationColumn> aggregations = [AggregationColumn.Count(CountAlias)];
        var section = false;
        if (spec.RecordsPerGroup is { } limit)
        {
            // A section's rows share their parent's document, key and watermark, so their positions ride
            // along too, and the position breaks the watermark's ties: every array then follows one total
            // order — the order the other stores give — and a limit that cuts among one parent's rows keeps
            // its first ones, not whichever the server reaches first.
            var table = await _client.Schema.GetTableAsync(tableName, cancellationToken).ConfigureAwait(false);
            section = table?.Columns.Any(c => c.Name == ProjectionSystemColumns.Row) == true;
            var order = section ? $"{ProjectionSystemColumns.Watermark}, {ProjectionSystemColumns.Row}" : ProjectionSystemColumns.Watermark;
            aggregations.Add(AggregationColumn.ArrayAgg(ProjectionSystemColumns.DocumentId, DocumentsAlias, limit, orderBy: order));
            aggregations.Add(AggregationColumn.ArrayAgg(ProjectionSystemColumns.RecordKey, KeysAlias, limit, orderBy: order));
            if (section)
            {
                aggregations.Add(AggregationColumn.ArrayAgg(ProjectionSystemColumns.Row, RowsAlias, limit, orderBy: order));
            }
        }

        for (var i = 0; i < measures.Count; i++)
        {
            var column = measures[i].Column;
            aggregations.Add(measures[i].Function switch
            {
                MeasureFunction.Sum => AggregationColumn.Sum(column, MeasureAlias(i)),
                MeasureFunction.Average => AggregationColumn.Avg(column, MeasureAlias(i)),
                MeasureFunction.Min => AggregationColumn.Min(column, MeasureAlias(i)),
                MeasureFunction.Max => AggregationColumn.Max(column, MeasureAlias(i)),
                MeasureFunction.CountDistinct => AggregationColumn.CountDistinct(column, MeasureAlias(i)),
                _ => throw new ArgumentOutOfRangeException(nameof(spec), measures[i].Function, "Unmapped measure function."),
            });
            aggregations.Add(AggregationColumn.Count(column, MeasureCountAlias(i)));
        }

        var request = new AggregationRequest
        {
            Aggregations = aggregations,
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
                    : Records(
                        row.TryGetValue(DocumentsAlias, out var documents) ? documents : null,
                        row.TryGetValue(KeysAlias, out var keys) ? keys : null,
                        section ? (row.TryGetValue(RowsAlias, out var positions) ? positions : null) : null,
                        section),
                measures
                    .Select((measure, i) => new MeasureValue(
                        measure,
                        row.TryGetValue(MeasureAlias(i), out var value) ? value : null,
                        row.TryGetValue(MeasureCountAlias(i), out var count) && count is not null ? Convert.ToInt64(count, CultureInfo.InvariantCulture) : 0))
                    .ToList()))
            .ToList();
    }

    /// <summary>A measure's alias, by its place in the spec — prefixed like <see cref="CountAlias"/>, so no grouping column shares it.</summary>
    private static string MeasureAlias(int index) => "fb_measure_" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The alias of how many rows a measure was computed from.</summary>
    private static string MeasureCountAlias(int index) => "fb_measure_count_" + index.ToString(CultureInfo.InvariantCulture);

    /// <summary>The document ids' alias; prefixed like <see cref="CountAlias"/> so no grouping column shares it.</summary>
    private const string DocumentsAlias = "fb_documents";

    /// <summary>The record keys' alias; prefixed like <see cref="CountAlias"/> so no grouping column shares it.</summary>
    private const string KeysAlias = "fb_record_keys";

    /// <summary>A section's row positions' alias; prefixed like <see cref="CountAlias"/> so no grouping column shares it.</summary>
    private const string RowsAlias = "fb_rows";

    /// <summary>
    /// Pairs the arrays index by index. Arrays of different lengths mean the server did not keep a null
    /// key in its place — pairing them anyway would name the wrong record for a document, so the answer
    /// is refused instead.
    /// <para>
    /// All three arrays follow the same total order (watermark, then a section row's position), so they
    /// pair up index by index and come in the order the other stores give.
    /// </para>
    /// </summary>
    private static List<RecordRef> Records(object? documents, object? keys, object? positions, bool section)
    {
        var ids = Elements(documents);
        var names = Elements(keys);
        var rows = section ? Elements(positions) : null;
        if (ids.Count != names.Count || (rows is not null && rows.Count != ids.Count))
        {
            throw new InvalidOperationException(
                $"MorphDB answered {ids.Count} documents, {names.Count} record keys and {rows?.Count.ToString(CultureInfo.InvariantCulture) ?? "no"} row positions for one group; " +
                "they are read in the same order and must pair up.");
        }

        return
        [
            .. ids.Select((id, i) => new RecordRef(
                DocumentId.From(id is Guid guid ? guid : Guid.Parse(Convert.ToString(id, CultureInfo.InvariantCulture)!)),
                names[i] is { } key ? RecordKey.Create(Convert.ToString(key, CultureInfo.InvariantCulture)!) : null,
                rows?[i] is { } position ? Convert.ToInt32(position, CultureInfo.InvariantCulture) : null)),
        ];
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
        FormbaseOperator.In => MorphOperator.In,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "No MorphDB operator for this filter."),
    };
}
