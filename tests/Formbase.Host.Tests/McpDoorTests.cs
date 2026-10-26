using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Formbase.Host.Tests;

/// <summary>
/// The detection tests for the read-only MCP door: it opens only when asked, it asks for the host's own access key and
/// opens on no host that asks for none, it answers in the REST API's words, and it has no tool that writes.
/// </summary>
public sealed class McpDoorTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string Key = new('k', 32);
    private static readonly string[] TotalIsTwo = ["total:2"];
    private static readonly string[] NotAPair = ["nocolon"];

    private readonly WebApplicationFactory<Program> _factory;

    public McpDoorTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // The door reads its switch while the host is composed, as a deployment's environment and settings files are there by
    // then — so the test gives its settings as host settings, not as sources added after composition.
    private WebApplicationFactory<Program> Door(bool enabled = true, int? maxRows = null) =>
        _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Formbase:Access:Keys:0", Key);
            builder.UseSetting("Formbase:Mcp:Enabled", enabled ? "true" : "false");
            if (maxRows is { } rows)
            {
                builder.UseSetting("Formbase:Mcp:MaxRows", rows.ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        });

    private static HttpClient Authorized(WebApplicationFactory<Program> host, string? key = null)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key ?? Key);
        return client;
    }

    private static async Task<McpClient> McpAsync(WebApplicationFactory<Program> host, string? key = null)
    {
        var http = host.CreateClient();
        return await McpClient.CreateAsync(
            new HttpClientTransport(
                new HttpClientTransportOptions
                {
                    Endpoint = new Uri(http.BaseAddress!, "/mcp"),
                    TransportMode = HttpTransportMode.StreamableHttp,
                    AdditionalHeaders = key is null ? new Dictionary<string, string>() : new Dictionary<string, string> { ["Authorization"] = $"Bearer {key}" },
                },
                http,
                ownsHttpClient: true),
            cancellationToken: TestContext.Current.CancellationToken);
    }

    private static async Task<string> SeedAsync(WebApplicationFactory<Program> host, params int[] totals)
    {
        var type = $"mcp{Guid.NewGuid():N}"[..14];
        host.Services.GetRequiredService<InMemoryFieldHintSource>()
            .Declare(new FormTypeHints(FormTypeRef.Create(type), type, [new FieldHint("total", ColumnType.Integer)]));
        using var client = Authorized(host);
        foreach (var total in totals)
        {
            (await client.PostAsync($"/formtypes/{type}/documents", new StringContent($$"""{"total":{{total}}}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken))
                .StatusCode.Should().Be(HttpStatusCode.Created);
        }

        (await client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);
        return type;
    }

    private static string Text(CallToolResult result) => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    // The default deployment's surface must not grow: with a key and no switch, /mcp is not there.
    [Fact]
    public async Task The_door_is_closed_unless_it_is_switched_on()
    {
        using var client = Authorized(Door(enabled: false));

        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Delete the access-key check in front of the door and this goes green on a tool list for anyone.
    [Fact]
    public async Task A_request_without_the_access_key_is_refused_as_on_the_rest_api()
    {
        using var client = Door().CreateClient();

        var response = await client.PostAsync("/mcp", new StringContent("{}", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // A host that asks for no key may be open by choice; an open door agents call by themselves is not that choice.
    [Fact]
    public void The_door_is_not_opened_on_a_host_that_asks_for_no_key()
    {
        var open = _factory.WithWebHostBuilder(builder => builder.UseSetting("Formbase:Mcp:Enabled", "true"));

        var start = () => open.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("Formbase:Access:Keys");
    }

    // Add a tool and this goes red until the list is changed here, on purpose; a write tool cannot arrive unseen.
    [Fact]
    public async Task The_door_offers_only_read_tools_and_every_one_says_it_is_read_only()
    {
        await using var client = await McpAsync(Door(), Key);

        var tools = await client.ListToolsAsync(cancellationToken: TestContext.Current.CancellationToken);

        tools.Select(t => t.Name).Order().Should().Equal("describe_form_type", "projection_status", "query_records");
        tools.Should().OnlyContain(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true && t.ProtocolTool.Annotations.DestructiveHint != true);
    }

    [Fact]
    public async Task Records_read_through_the_door_are_the_records_the_rest_api_gives()
    {
        var host = Door();
        var type = await SeedAsync(host, 1, 2, 3);
        await using var client = await McpAsync(host, Key);
        var ct = TestContext.Current.CancellationToken;

        var read = await client.CallToolAsync("query_records", new Dictionary<string, object?> { ["type"] = type, ["filter"] = TotalIsTwo }, cancellationToken: ct);

        read.IsError.Should().NotBe(true, Text(read));
        var result = read.StructuredContent!.Value.GetProperty("result");
        result.GetProperty("stale").GetBoolean().Should().BeFalse();
        result.GetProperty("rows").EnumerateArray().Select(r => r.GetProperty("fields").GetProperty("total").GetInt64()).Should().Equal(2L);
    }

    [Fact]
    public async Task A_window_is_capped_by_the_door_and_the_cap_is_said()
    {
        var host = Door(maxRows: 2);
        var type = await SeedAsync(host, 1, 2, 3);
        await using var client = await McpAsync(host, Key);

        var read = await client.CallToolAsync("query_records", new Dictionary<string, object?> { ["type"] = type, ["limit"] = 50, ["orderBy"] = "total" }, cancellationToken: TestContext.Current.CancellationToken);

        var page = read.StructuredContent!.Value;
        page.GetProperty("limit").GetInt32().Should().Be(2);
        page.GetProperty("result").GetProperty("rows").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task The_declaration_and_the_projections_state_are_read_in_the_rest_apis_shape()
    {
        var host = Door();
        var type = await SeedAsync(host, 1);
        await using var client = await McpAsync(host, Key);
        var ct = TestContext.Current.CancellationToken;

        var declaration = await client.CallToolAsync("describe_form_type", new Dictionary<string, object?> { ["type"] = type }, cancellationToken: ct);
        var status = await client.CallToolAsync("projection_status", new Dictionary<string, object?> { ["type"] = type }, cancellationToken: ct);

        Text(declaration).Should().Contain("\"total\"");
        Text(status).Should().Contain("projected");
    }

    [Fact]
    public async Task What_the_rest_api_refuses_is_the_tools_error_in_the_same_words()
    {
        var host = Door();
        var type = $"mcp{Guid.NewGuid():N}"[..14];
        await using var client = await McpAsync(host, Key);
        var ct = TestContext.Current.CancellationToken;

        var neverProjected = await client.CallToolAsync("query_records", new Dictionary<string, object?> { ["type"] = type }, cancellationToken: ct);
        var badFilter = await client.CallToolAsync("query_records", new Dictionary<string, object?> { ["type"] = type, ["filter"] = NotAPair }, cancellationToken: ct);
        var undeclared = await client.CallToolAsync("describe_form_type", new Dictionary<string, object?> { ["type"] = type }, cancellationToken: ct);

        neverProjected.IsError.Should().BeTrue();
        badFilter.IsError.Should().BeTrue();
        Text(badFilter).Should().Contain("not a `column:value` pair");
        undeclared.IsError.Should().BeTrue();
        Text(undeclared).Should().Contain("no declaration");
    }
}
