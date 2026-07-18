using Formbase.Core.Ports;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.InMemory;

/// <summary>
/// In-process <see cref="IProjectionStore"/> — the reference target for projection, and the store the
/// projector/record-query are tested against without a real database. Mirrors the drop-and-rebuild
/// protocol: create fails if the table exists, insert/query fail if it does not.
/// </summary>
public sealed class InMemoryProjectionStore : IProjectionStore
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Table> _tables = new(StringComparer.Ordinal);

    public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_tables.ContainsKey(tableName));
        }
    }

    public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _tables.Remove(tableName);
            return Task.CompletedTask;
        }
    }

    public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_tables.ContainsKey(schema.TableName))
            {
                throw new InvalidOperationException($"Table '{schema.TableName}' already exists; drop it before creating.");
            }

            _tables[schema.TableName] = new Table(schema);
            return Task.CompletedTask;
        }
    }

    public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var table = Require(tableName);
            foreach (var row in rows)
            {
                table.Rows.Add(new Dictionary<string, object?>(row, StringComparer.Ordinal));
            }

            return Task.FromResult(rows.Count);
        }
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var table = Require(tableName);
            IEnumerable<Dictionary<string, object?>> query = table.Rows;

            if (spec.Filters is { Count: > 0 } filters)
            {
                query = query.Where(row => filters.All(f => Matches(row, f.Key, f.Value)));
            }

            if (spec.Offset is { } offset)
            {
                query = query.Skip(offset);
            }

            if (spec.Limit is { } limit)
            {
                query = query.Take(limit);
            }

            IReadOnlyList<IReadOnlyDictionary<string, object?>> result = query
                .Select(row => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(row, StringComparer.Ordinal))
                .ToList();

            return Task.FromResult(result);
        }
    }

    private static bool Matches(Dictionary<string, object?> row, string column, object? expected)
        => row.TryGetValue(column, out var actual) && Equals(actual, expected);

    private Table Require(string tableName)
        => _tables.TryGetValue(tableName, out var table)
            ? table
            : throw new InvalidOperationException($"Table '{tableName}' does not exist.");

    private sealed class Table(TableSchema schema)
    {
        public TableSchema Schema { get; } = schema;

        public List<Dictionary<string, object?>> Rows { get; } = [];
    }
}
