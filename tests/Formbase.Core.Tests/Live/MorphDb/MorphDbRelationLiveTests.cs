using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;
using Formbase.Core.Schema;
using Formbase.MorphDb;
using MorphDB.Client;
using MorphDB.Client.Models;
using Testcontainers.PostgreSql;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// Exercises relation materialization (<c>RelationKind.Child</c>) against a real MorphDB. Deliberately
/// not on <see cref="MorphDbCollection"/>: that fixture pins the published compatibility pair
/// (<c>0.9.x</c>, held to the README by <c>ReadmeInstallParityTests</c>), and relation materialization
/// needs the server fix that shipped in <c>0.10.0</c> — before it, <c>EnforceOnWrite: false</c> was
/// accepted and silently ignored, so a non-enforcing relation still got a physical FK and blocked the
/// drop half of drop-and-rebuild. Running this ahead of the pinned pair is the same shape as the
/// scheduled <c>morphdb-drift</c> workflow's forward-looking run against <c>latest</c>, pinned
/// explicitly instead of floating so the case this suite exists to catch stays reproducible.
/// Requires Docker (category: Live).
/// </summary>
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbRelationLiveTests : IAsyncLifetime
{
    private const string PostgresAlias = "postgres";
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromMinutes(2);

    private INetwork _network = null!;
    private PostgreSqlContainer _postgres = null!;
    private IContainer _morphdb = null!;
    private string _baseUrl = string.Empty;
    private Guid _projectId;
    private MorphDbProjectionStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        _network = new NetworkBuilder().Build();
        await _network.CreateAsync();

        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithNetwork(_network)
            .WithNetworkAliases(PostgresAlias)
            .WithDatabase("morphdb")
            .WithUsername("morph")
            .WithPassword("morph")
            .Build();
        await _postgres.StartAsync();

        _morphdb = new ContainerBuilder(
                Environment.GetEnvironmentVariable("FORMBASE_MORPHDB_IMAGE") ?? "ghcr.io/iyulab/morphdb:0.11.1")
            .WithNetwork(_network)
            .WithEnvironment("ConnectionStrings__MorphDB", $"Host={PostgresAlias};Port=5432;Database=morphdb;Username=morph;Password=morph")
            .WithEnvironment("ASPNETCORE_ENVIRONMENT", "Production")
            .WithPortBinding(8080, assignRandomHostPort: true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(r => r.ForPort(8080).ForPath("/health"), o => o.WithTimeout(ReadinessTimeout)))
            .Build();
        await _morphdb.StartAsync();

        _baseUrl = $"http://{_morphdb.Hostname}:{_morphdb.GetMappedPublicPort(8080)}";

        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = ReadinessTimeout };
        var response = await http.PostAsJsonAsync("/api/projects", new { name = $"formbase-relation-live-{Guid.NewGuid():N}" });
        response.EnsureSuccessStatusCode();
        var project = await response.Content.ReadFromJsonAsync<ProvisionedProject>()
            ?? throw new InvalidOperationException("MorphDB returned no body for the project provisioning request.");

        _projectId = project.Id;
        _store = new MorphDbProjectionStore(CreateRawClient());
    }

    public async ValueTask DisposeAsync()
    {
        await _morphdb.DisposeAsync();
        await _postgres.DisposeAsync();
        await _network.DeleteAsync();
    }

    /// <summary>
    /// The order the core projector actually produces: a form type is projected independently of the
    /// types its relations name, so the parent's <c>CreateTableAsync</c> — carrying the relation — can
    /// run before the child table exists at all (this fixture's own multi-lot regression test projects
    /// the parent notice first, the child lots second). That must not throw; the relation is expected
    /// to be absent until the child appears, not an error to surface as a failed projection.
    /// </summary>
    [Fact]
    public async Task Projecting_the_parent_before_the_child_exists_does_not_throw()
    {
        const string notices = "notices_before_child";
        var noticeSchema = NoticeSchema(notices);

        var act = () => _store.CreateTableAsync(noticeSchema);

        await act.Should().NotThrowAsync(
            "the child table the relation names has not been projected yet on a first run, which is expected");

        var relationNames = await QueryRelationNamesAsync(notices);
        relationNames.Should().BeEmpty(
            "the relation was skipped, not materialized with a placeholder or a broken reference");
    }

    /// <summary>
    /// Once both sides exist, redeclaring the parent (its own drop-and-rebuild) must materialize the
    /// relation rather than silently continuing to skip it — this is what proves the "reappears on the
    /// next rebuild" claim in <c>MorphDbProjectionStore.MaterializeRelationAsync</c>'s doc comment,
    /// not just that nothing throws.
    /// </summary>
    [Fact]
    public async Task Redeclaring_the_parent_after_the_child_exists_materializes_the_relation()
    {
        const string notices = "notices_after_child";
        const string lots = "lots_after_child";

        await _store.CreateTableAsync(NoticeSchema(notices, lots), TestContext.Current.CancellationToken);
        await _store.CreateTableAsync(LotSchema(lots), TestContext.Current.CancellationToken);

        // Redeclare the parent now that the child exists — same drop-then-create the core projector
        // does on every cycle (Projector.cs) — this is the call that should succeed in creating the
        // relation MorphDB never got on the first pass.
        await _store.DropTableAsync(notices, TestContext.Current.CancellationToken);
        await _store.CreateTableAsync(NoticeSchema(notices, lots), TestContext.Current.CancellationToken);

        // The REST client exposes no relation-read endpoint, so read the relation back the way
        // docs/API.md's own "Reading the schema" section does: GraphQL's `table(name).relations`.
        var relationNames = await QueryRelationNamesAsync(notices);

        relationNames.Should().Contain("lots",
            "the redeclare above ran after the child table existed, so the relation should have " +
            "materialized rather than staying skipped");
    }

    /// <summary>
    /// The adapter's own drop-and-rebuild, twice in a row, with the relation materializing on the
    /// second pass each time. This is what would surface a lingering-inactive-relation conflict on
    /// re-creation (the "relation lifecycle" question the original client-surface issue left open) —
    /// if the second full round throws, that is the answer, empirically rather than by inspection.
    /// </summary>
    [Fact]
    public async Task A_second_full_drop_and_rebuild_round_does_not_conflict_with_the_first()
    {
        const string notices = "notices_round_trip";
        const string lots = "lots_round_trip";

        // One full cycle: both tables (re)created, then the parent redeclared once more so the
        // relation materializes now that the child exists — the same three-step shape as the
        // "materializes" test above, run twice back to back.
        async Task ProjectOnceAsync()
        {
            await _store.DropTableAsync(notices);
            await _store.CreateTableAsync(NoticeSchema(notices, lots));
            await _store.DropTableAsync(lots);
            await _store.CreateTableAsync(LotSchema(lots));
            await _store.DropTableAsync(notices);
            await _store.CreateTableAsync(NoticeSchema(notices, lots));
        }

        await ProjectOnceAsync();

        var act = () => ProjectOnceAsync();

        await act.Should().NotThrowAsync(
            "a second drop-and-rebuild round is the normal case (every subsequent projection), not an edge case");
    }

    private MorphDBClient CreateRawClient() => new(_baseUrl, new MorphDBClientOptions { ProjectId = _projectId });

    /// <summary>
    /// The REST schema client exposes no relation-read endpoint (<see cref="SchemaClient"/> only
    /// creates and deletes them), so this reads them back the way docs/API.md's own "Reading the
    /// schema" section does: GraphQL's <c>table(name).relations</c>.
    /// </summary>
    private async Task<IReadOnlyList<string>> QueryRelationNamesAsync(string tableName)
    {
        using var http = new HttpClient { BaseAddress = new Uri(_baseUrl) };
        http.DefaultRequestHeaders.Add("X-Project-Id", _projectId.ToString());

        var query = new
        {
            query = $$"""
                query { table(name: "{{tableName}}") { relations { name } } }
                """,
        };

        var response = await http.PostAsJsonAsync("/graphql", query);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadFromJsonAsync<GraphQlTableResponse>()
            ?? throw new InvalidOperationException("GraphQL returned no body for the relations query.");

        return body.Data?.Table?.Relations?.Select(r => r.Name).ToList() ?? [];
    }

    private sealed record GraphQlTableResponse([property: JsonPropertyName("data")] GraphQlData? Data);

    private sealed record GraphQlData([property: JsonPropertyName("table")] GraphQlTable? Table);

    private sealed record GraphQlTable([property: JsonPropertyName("relations")] IReadOnlyList<GraphQlRelation>? Relations);

    private sealed record GraphQlRelation([property: JsonPropertyName("name")] string Name);

    private static TableSchema NoticeSchema(string noticeTable, string lotTable = "lots") => new(
        noticeTable,
        [
            new ColumnDef("noticeId", ColumnType.Text, Nullable: false),
            new ColumnDef("buyerName", ColumnType.Text),
        ],
        [new RelationDef("lots", RelationKind.Child, lotTable, "noticeId")]);

    private static TableSchema LotSchema(string lotTable) => new(
        lotTable,
        [
            new ColumnDef("lotId", ColumnType.Text, Nullable: false),
            new ColumnDef("noticeId", ColumnType.Text, Nullable: false),
        ]);

    private sealed record ProvisionedProject([property: JsonPropertyName("id")] Guid Id);
}
