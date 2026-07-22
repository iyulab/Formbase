using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using MorphDB.Client;
using Testcontainers.PostgreSql;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// Provides a ready-to-use MorphDB service for the live tests: by default a real service plus its
/// PostgreSQL, both started on a shared Docker network and shared by every live test class. Setting
/// <see cref="BaseUrlVariable"/> points the suite at an already-running service instead (CI service
/// container, local compose) and skips container management entirely.
/// <para>
/// Every MorphDB schema and data request is scoped to a project that must exist first, so the fixture
/// provisions one and hands clients its id. MorphDB ensures its own global schema at startup and retries
/// while its database is still coming up, so no ordering is forced here.
/// </para>
/// </summary>
public sealed class MorphDbFixture : IAsyncLifetime
{
    /// <summary>Points the live suite at an external MorphDB instead of starting one.</summary>
    public const string BaseUrlVariable = "FORMBASE_MORPHDB_URL";

    private const string PostgresAlias = "postgres";

    /// <summary>Bounds the readiness wait so an unreachable service fails the run instead of stalling it.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    private readonly INetwork? _network;
    private PostgreSqlContainer? _postgres;
    private IContainer? _morphdb;

    public MorphDbFixture()
    {
        BaseUrl = Environment.GetEnvironmentVariable(BaseUrlVariable)?.TrimEnd('/') ?? string.Empty;
        _network = BaseUrl.Length == 0 ? new NetworkBuilder().Build() : null;
    }

    public string BaseUrl { get; private set; }

    /// <summary>The provisioned project every client is scoped to.</summary>
    public Guid ProjectId { get; private set; }

    public MorphDBClient CreateClient() => new(BaseUrl, new MorphDBClientOptions { ProjectId = ProjectId });

    public async Task InitializeAsync()
    {
        if (_network is not null)
        {
            await StartServiceAsync(_network);
        }

        ProjectId = await ProvisionProjectAsync();
    }

    private async Task StartServiceAsync(INetwork network)
    {
        await network.CreateAsync();

        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithNetwork(network)
            .WithNetworkAliases(PostgresAlias)
            .WithDatabase("morphdb")
            .WithUsername("morph")
            .WithPassword("morph")
            .Build();
        await _postgres.StartAsync();

        // Pinned to the compatibility pair (Formbase.* 0.4.x ↔ MorphDB 0.8.x): CI must test the
        // contract this code actually targets, not whatever `latest` became overnight. Drift against
        // newer server releases is watched separately (the scheduled morphdb-drift workflow runs
        // this same suite with FORMBASE_MORPHDB_IMAGE=ghcr.io/iyulab/morphdb:latest).
        _morphdb = new ContainerBuilder(
                Environment.GetEnvironmentVariable("FORMBASE_MORPHDB_IMAGE") ?? "ghcr.io/iyulab/morphdb:0.8.0")
            .WithNetwork(network)
            .WithEnvironment("ConnectionStrings__MorphDB", $"Host={PostgresAlias};Port=5432;Database=morphdb;Username=morph;Password=morph")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health"), o => o.WithTimeout(ReadinessTimeout)))
            .Build();
        await _morphdb.StartAsync();

        BaseUrl = $"http://{_morphdb.Hostname}:{_morphdb.GetMappedPublicPort(8080)}";
    }

    /// <summary>
    /// Creates the project MorphDB provisions schemas for. The name is unique per run so the
    /// external-service mode does not collide with leftovers from an earlier run.
    /// </summary>
    private async Task<Guid> ProvisionProjectAsync()
    {
        using var http = new HttpClient { BaseAddress = new Uri(BaseUrl), Timeout = ReadinessTimeout };

        var response = await http.PostAsJsonAsync(
            "/api/projects",
            new { name = $"formbase-live-{Guid.NewGuid():N}" });

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"MorphDB rejected the project provisioning request ({(int)response.StatusCode}): {body}");
        }

        var project = await response.Content.ReadFromJsonAsync<ProvisionedProject>()
            ?? throw new InvalidOperationException("MorphDB returned no body for the project provisioning request.");

        return project.Id;
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

        if (_network is not null)
        {
            await _network.DeleteAsync();
        }
    }

    private sealed record ProvisionedProject([property: JsonPropertyName("id")] Guid Id);
}

/// <summary>Shares one MorphDB container across all live test classes.</summary>
[CollectionDefinition(Name)]
public sealed class MorphDbCollection : ICollectionFixture<MorphDbFixture>
{
    public const string Name = "morphdb-live";
}
