using Formbase.Core.Ports;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Integration;

/// <summary>
/// Test decorator that simulates a backing-store outage: when <see cref="IsAvailable"/> is false,
/// every operation throws, as a real MorphDB service would when unreachable.
/// </summary>
internal sealed class ToggleableProjectionStore : IProjectionStore
{
    private readonly IProjectionStore _inner;

    public ToggleableProjectionStore(IProjectionStore inner) => _inner = inner;

    public bool IsAvailable { get; set; } = true;

    private void GuardAvailable()
    {
        if (!IsAvailable)
        {
            throw new TimeoutException("projection store is unavailable");
        }
    }

    public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default)
    {
        GuardAvailable();
        return _inner.TableExistsAsync(tableName, cancellationToken);
    }

    public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default)
    {
        GuardAvailable();
        return _inner.DropTableAsync(tableName, cancellationToken);
    }

    public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default)
    {
        GuardAvailable();
        return _inner.CreateTableAsync(schema, cancellationToken);
    }

    public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
    {
        GuardAvailable();
        return _inner.BulkInsertAsync(tableName, rows, cancellationToken);
    }

    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
    {
        GuardAvailable();
        return _inner.QueryAsync(tableName, spec, cancellationToken);
    }
}
