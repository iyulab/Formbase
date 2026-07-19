using Npgsql;
using Testcontainers.PostgreSql;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// Spins up a plain PostgreSQL once for all raw-store live tests and exposes a shared data source. Each
/// test isolates itself in a fresh schema (see the contract subclass), so one container serves them all.
/// Requires Docker (category: Live).
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer _postgres = null!;

    public NpgsqlDataSource DataSource { get; private set; } = null!;

    /// <summary>
    /// Builds a data source with its own connection pool, independent of <see cref="DataSource"/>. Stores
    /// built over separate pools share no in-process state, which is how a multi-process cold start is
    /// approximated in a single test host. The caller disposes what it creates.
    /// </summary>
    public NpgsqlDataSource CreateIndependentDataSource() => NpgsqlDataSource.Create(_postgres.GetConnectionString());

    public async Task InitializeAsync()
    {
        _postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await _postgres.StartAsync();
        DataSource = NpgsqlDataSource.Create(_postgres.GetConnectionString());
    }

    public async Task DisposeAsync()
    {
        if (DataSource is not null)
        {
            await DataSource.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }
}

/// <summary>Shares one PostgreSQL container across all raw-store live test classes.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresCollection : ICollectionFixture<PostgresFixture>
{
    public const string Name = "postgres-live";
}
