using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
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

    public Task<int> ReplaceRowsAsync(
        string tableName,
        IReadOnlyCollection<RecordKey> removeKeys,
        IReadOnlyCollection<DocumentId> removeDocuments,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default)
    {
        var keys = removeKeys.Select(k => k.Value).ToHashSet(StringComparer.Ordinal);
        var documents = removeDocuments.Select(d => d.Value).ToHashSet();
        lock (_gate)
        {
            var table = Require(tableName);
            table.Rows.RemoveAll(row =>
                (row.TryGetValue(ProjectionSystemColumns.RecordKey, out var key) && key is string k && keys.Contains(k))
                || (row.TryGetValue(ProjectionSystemColumns.DocumentId, out var id) && id is Guid g && documents.Contains(g)));
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
                query = query.Where(row => filters.All(f => Matches(row, f)));
            }

            if (spec.OrderBy is { Count: > 0 } orderBy)
            {
                query = ApplyOrder(query, orderBy);
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

    public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var table = Require(tableName);
            var groupBy = spec.GroupBy ?? [];
            var kept = spec.Filters is { Count: > 0 } filters
                ? table.Rows.Where(row => filters.All(f => Matches(row, f))).ToList()
                : table.Rows;

            if (groupBy.Count == 0)
            {
                // Ungrouped counts are one answer even when nothing is kept — COUNT(*) over no rows is 0,
                // not an absent row.
                IReadOnlyList<AggregateGroup> total = [new AggregateGroup(new Dictionary<string, object?>(StringComparer.Ordinal), kept.Count)];
                return Task.FromResult(total);
            }

            IReadOnlyList<AggregateGroup> groups = kept
                .GroupBy(row => new GroupKey(groupBy.Select(column => row.GetValueOrDefault(column)).ToArray()))
                .Select(group => new AggregateGroup(
                    groupBy.Select((column, i) => (column, value: group.Key.Values[i]))
                        .ToDictionary(pair => pair.column, pair => pair.value, StringComparer.Ordinal),
                    group.LongCount()))
                .ToList();

            return Task.FromResult(groups);
        }
    }

    private static IEnumerable<Dictionary<string, object?>> ApplyOrder(
        IEnumerable<Dictionary<string, object?>> query, IReadOnlyList<OrderKey> orderBy)
    {
        IOrderedEnumerable<Dictionary<string, object?>>? ordered = null;
        foreach (var key in orderBy)
        {
            var column = key.Column;
            object? Selector(Dictionary<string, object?> row) => row.GetValueOrDefault(column);

            ordered = ordered is null
                ? (key.Descending ? query.OrderByDescending(Selector, ValueOrder.Comparer) : query.OrderBy(Selector, ValueOrder.Comparer))
                : (key.Descending ? ordered.ThenByDescending(Selector, ValueOrder.Comparer) : ordered.ThenBy(Selector, ValueOrder.Comparer));
        }

        return ordered ?? query;
    }

    /// <summary>
    /// SQL's reading of a filter, which is the one a real store gives: a null column matches only an
    /// equality with null, text matching ignores case, and a value of another type than the column's
    /// matches nothing (the caller has coerced values to the declared type already).
    /// </summary>
    private static bool Matches(Dictionary<string, object?> row, FieldFilter filter)
    {
        row.TryGetValue(filter.Column, out var actual);

        switch (filter.Operator)
        {
            case FilterOperator.Equal:
                return Equals(actual, filter.Value);
            case FilterOperator.IsNull:
                return actual is null;
            case FilterOperator.IsNotNull:
                return actual is not null;
        }

        if (actual is null || filter.Value is null)
        {
            return false;
        }

        return filter.Operator switch
        {
            FilterOperator.Contains => actual is string text && filter.Value is string part
                && text.Contains(part, StringComparison.OrdinalIgnoreCase),
            FilterOperator.StartsWith => actual is string text && filter.Value is string prefix
                && text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase),
            _ => actual.GetType() == filter.Value.GetType() && filter.Operator switch
            {
                FilterOperator.GreaterThan => ValueOrder.Compare(actual, filter.Value) > 0,
                FilterOperator.GreaterThanOrEqual => ValueOrder.Compare(actual, filter.Value) >= 0,
                FilterOperator.LessThan => ValueOrder.Compare(actual, filter.Value) < 0,
                FilterOperator.LessThanOrEqual => ValueOrder.Compare(actual, filter.Value) <= 0,
                _ => false,
            },
        };
    }

    /// <summary>A group's key values, compared element by element so equal combinations form one group.</summary>
    private sealed class GroupKey(object?[] values) : IEquatable<GroupKey>
    {
        public object?[] Values { get; } = values;

        public bool Equals(GroupKey? other) =>
            other is not null && Values.Length == other.Values.Length && Values.Zip(other.Values).All(pair => Equals(pair.First, pair.Second));

        public override bool Equals(object? obj) => Equals(obj as GroupKey);

        public override int GetHashCode()
        {
            var hash = new HashCode();
            foreach (var value in Values)
            {
                hash.Add(value);
            }

            return hash.ToHashCode();
        }
    }

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
