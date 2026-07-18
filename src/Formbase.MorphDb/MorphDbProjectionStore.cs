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
            var request = new BatchRequest
            {
                Inserts = chunk
                    .Select(r => (IDictionary<string, object?>)new Dictionary<string, object?>(r, StringComparer.Ordinal))
                    .ToList(),
            };

            var response = await _client.Data.BatchAsync(tableName, request, cancellationToken).ConfigureAwait(false);
            inserted += response.Inserted.Count;
        }

        return inserted;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        var pageSize = spec.Limit ?? DefaultPageSize;
        var offset = spec.Offset ?? 0;

        var request = new QueryRequest
        {
            Filters = spec.Filters is { Count: > 0 } filters
                ? filters.Select(f => new Filter { Column = f.Key, Operator = FilterOperator.Equal, Value = f.Value }).ToList()
                : [],
            PageSize = pageSize,
            // MorphDB pages are 1-based; exact offset requires it to align to the page size.
            Page = pageSize > 0 ? (offset / pageSize) + 1 : 1,
        };

        var paged = await _client.Data.QueryAsync(tableName, request, cancellationToken).ConfigureAwait(false);

        return paged.Data
            .Select(record => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(record.Data, StringComparer.Ordinal))
            .ToList();
    }
}
