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
        // declaration semantics MorphDB has no notion of. Declared relations are delivered to this
        // port but NOT materialized as MorphDB virtual FKs at stage-1: the MorphDB.Client 0.9.0
        // exposes no relations API to wrap, and whether a rebuildable projection should carry
        // enforced FKs is an open design question. The FK column data still projects as a normal
        // column, so the projection stays complete — only the optional relation link is absent.
        // Tracked: claudedocs/morphdb/issues/ISSUE-morphdb-20260724-client-lacks-relations-api.md.
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
