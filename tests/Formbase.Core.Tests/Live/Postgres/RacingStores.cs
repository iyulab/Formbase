using Formbase.Postgres;
using Npgsql;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// A set of stores over one schema that can be released at the same instant, for tests about what
/// happens when several of them reach Postgres together.
/// <para>
/// Every store gets its <b>own data source</b>, hence its own connection pool and no shared in-process
/// state — a store's private init gate and any other instance-local guard are therefore useless here,
/// which is the point: only database-scoped serialization can hold. This is as close to separate
/// processes as a single test host gets.
/// </para>
/// </summary>
internal sealed class RacingStores : IAsyncDisposable
{
    private readonly NpgsqlDataSource[] _dataSources;
    private readonly PostgresRawStore[] _stores;

    public RacingStores(PostgresFixture fixture, string schema, int count)
    {
        _dataSources = [.. Enumerable.Range(0, count).Select(_ => fixture.CreateIndependentDataSource())];
        _stores = [.. _dataSources.Select(source => new PostgresRawStore(source, schema))];
    }

    /// <summary>
    /// Releases all racers at the same moment. Without the barrier they would start staggered by
    /// however long each first call took to get going, and the window under test could close before
    /// it opens.
    /// </summary>
    public async Task<T[]> RaceAsync<T>(Func<PostgresRawStore, Task<T>> action)
    {
        using var startLine = new SemaphoreSlim(0, _stores.Length);
        var runs = _stores.Select(async store =>
        {
            await startLine.WaitAsync();
            return await action(store);
        }).ToArray();

        startLine.Release(_stores.Length);
        return await Task.WhenAll(runs);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var store in _stores)
        {
            store.Dispose();
        }

        foreach (var dataSource in _dataSources)
        {
            await dataSource.DisposeAsync();
        }
    }
}
