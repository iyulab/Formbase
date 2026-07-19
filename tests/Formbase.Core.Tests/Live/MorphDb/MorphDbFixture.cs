using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using MorphDB.Client;
using Testcontainers.PostgreSql;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// Spins up a real MorphDB service backed by PostgreSQL, both on a shared Docker network, once for
/// all live tests. MorphDB ensures its own schema/extensions at startup, so a plain postgres suffices.
/// <para>
/// Incomplete on purpose: MorphDB's <c>/health</c> reports 503 while its optional redis dependency is
/// absent, so readiness is never reached with this harness alone (a redis service and a seeded tenant
/// are still missing). The readiness wait is therefore bounded — Testcontainers otherwise retries for
/// its one-hour default, which stalls a CI job rather than failing it.
/// </para>
/// </summary>
public sealed class MorphDbFixture : IAsyncLifetime
{
    private const string PostgresAlias = "postgres";

    /// <summary>Bounds the readiness wait so an unreachable service fails the run instead of stalling it.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    private readonly INetwork _network = new NetworkBuilder().Build();
    private PostgreSqlContainer _postgres = null!;
    private IContainer _morphdb = null!;

    public string BaseUrl { get; private set; } = string.Empty;

    public MorphDBClient CreateClient() => new(BaseUrl);

    public async Task InitializeAsync()
    {
        await _network.CreateAsync();

        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(PostgresAlias)
            .WithDatabase("morphdb")
            .WithUsername("morph")
            .WithPassword("morph")
            .Build();
        await _postgres.StartAsync();

        _morphdb = new ContainerBuilder("ghcr.io/iyulab/morphdb:latest")
            .WithNetwork(_network)
            .WithEnvironment("ConnectionStrings__MorphDB", $"Host={PostgresAlias};Port=5432;Database=morphdb;Username=morph;Password=morph")
            .WithEnvironment("Jwt__SecretKey", "MorphDB-Testcontainers-Secret-Key-Must-Be-At-Least-32-Characters")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health"), o => o.WithTimeout(ReadinessTimeout)))
            .Build();
        await _morphdb.StartAsync();

        BaseUrl = $"http://{_morphdb.Hostname}:{_morphdb.GetMappedPublicPort(8080)}";
    }

    public async Task DisposeAsync()
    {
        if (_morphdb is not null)
        {
            await _morphdb.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }

        await _network.DeleteAsync();
    }
}

/// <summary>Shares one MorphDB container across all live test classes.</summary>
[CollectionDefinition(Name)]
public sealed class MorphDbCollection : ICollectionFixture<MorphDbFixture>
{
    public const string Name = "morphdb-live";
}
