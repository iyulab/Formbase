using Formbase.Core.Ports;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using MorphDB.Client;
using MorphDB.Client.Models;

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

    /// <summary>MorphDB caps a page at this many rows, so a window wider than it spans several pages.</summary>
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
    /// analysis settled on non-enforcing metadata for exactly that reason
    /// (<c>claudedocs/Formbase/plans/2026-07-24-projection-fk-enforcement-analysis.md</c>). No
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

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        var limit = spec.Limit ?? DefaultPageSize;
        var offset = spec.Offset ?? 0;

        if (limit <= 0)
        {
            return [];
        }

        // MorphDB pages instead of taking an offset, so an arbitrary offset has to be assembled from the
        // pages that cover it. Sizing pages at the limit keeps that to two requests in the common case:
        // the window is never longer than a page, so it straddles at most a page boundary.
        var pageSize = Math.Min(limit, MaxPageSize);
        var firstPage = (offset / pageSize) + 1;
        var skip = offset % pageSize;
        var needed = skip + limit;

        var filters = spec.Filters is { Count: > 0 } specFilters
            ? specFilters.Select(f => new Filter(f.Key, FilterOperator.Equal, f.Value)).ToList()
            : [];

        // Server-side ordering — the only way paging is deterministic (a client-side sort would order
        // an already-arbitrary page). Descending maps to ascending: false.
        var orderBy = spec.OrderBy is { Count: > 0 } specOrder
            ? specOrder.Select(k => new OrderBy(k.Column, ascending: !k.Descending)).ToList()
            : [];

        var window = new List<IReadOnlyDictionary<string, object?>>(needed);
        for (var page = firstPage; window.Count < needed; page++)
        {
            var request = new QueryRequest
            {
                Filters = filters,
                OrderBy = orderBy,
                PageSize = pageSize,
                Page = page,
            };

            var paged = await _client.Data
                .QueryAsync(tableName, request, cancellationToken)
                .ConfigureAwait(false);

            window.AddRange(paged.Data
                .Select(record => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(record.Data, StringComparer.Ordinal)));

            // A short page is the last one — asking for more would loop forever on an exhausted table.
            if (paged.Data.Count < pageSize)
            {
                break;
            }
        }

        return window.Skip(skip).Take(limit).ToList();
    }
}
