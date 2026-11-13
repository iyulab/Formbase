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

        tools.Select(t => t.Name).Order().Should().Equal("describe_form_type", "get_document", "list_documents", "projection_status", "query_records");
        tools.Should().OnlyContain(t => t.ProtocolTool.Annotations!.ReadOnlyHint == true && t.ProtocolTool.Annotations.DestructiveHint != true);
    }

    // A model's usual mistake is a name: `filters` for `filter`. Unread, it narrows nothing and the answer is every record.
    [Fact]
    public async Task A_misspelled_argument_is_refused_and_no_records_come_back()
    {
        var host = Door();
        var type = await SeedAsync(host, 1, 2, 3);
        await using var client = await McpAsync(host, Key);

        var typo = await client.CallToolAsync("query_records", new Dictionary<string, object?> { ["type"] = type, ["filters"] = TotalIsTwo }, cancellationToken: TestContext.Current.CancellationToken);

        typo.IsError.Should().BeTrue("the call asked for a narrowing the tool did not apply");
        Text(typo).Should().Contain("Unknown argument 'filters'").And.Contain("query_records")
            .And.Contain("filter", "the refusal lists the arguments the tool takes so the call can be sent again");
        typo.StructuredContent.Should().BeNull("a refused call returns no records");
    }

    // The SDK binds an argument by its exact name, so `Filter` is not `filter`: it used to pass the refusal and then narrow nothing.
    [Fact]
    public async Task An_argument_named_in_another_case_is_never_silently_ignored()
    {
        var host = Door();
        var type = await SeedAsync(host, 1, 2, 3);
        await using var client = await McpAsync(host, Key);

        var cased = await client.CallToolAsync("query_records", new Dictionary<string, object?> { ["type"] = type, ["Filter"] = TotalIsTwo }, cancellationToken: TestContext.Current.CancellationToken);

        cased.IsError.Should().BeTrue("a call whose narrowing was not applied must be refused, not answered with every record");
        Text(cased).Should().Contain("Unknown argument 'Filter'").And.Contain("did you mean 'filter'");
        cased.StructuredContent.Should().BeNull();
    }

    [Fact]
    public async Task Every_tool_refuses_an_argument_it_does_not_declare()
    {
        await using var client = await McpAsync(Door(), Key);
        var failures = new List<string>();

        foreach (var tool in Formbase.Mcp.FormbaseMcpTools.ToolNames)
        {
            var result = await client.CallToolAsync(tool, new Dictionary<string, object?> { ["stray"] = 1 }, cancellationToken: TestContext.Current.CancellationToken);
            if (result.IsError != true || !Text(result).Contains("Unknown argument 'stray'", StringComparison.Ordinal))
            {
                failures.Add($"{tool}: {(result.IsError == true ? Text(result) : "answered as if the argument had not been sent")}");
            }
        }

        failures.Should().BeEmpty(string.Join("\n", failures));
    }

    [Fact]
    public async Task The_arguments_a_tool_declares_still_pass()
    {
        var host = Door();
        var type = await SeedAsync(host, 1, 2, 3);
        await using var client = await McpAsync(host, Key);

        var read = await client.CallToolAsync("query_records", new Dictionary<string, object?>
        {
            ["type"] = type,
            ["filter"] = TotalIsTwo,
            ["orderBy"] = "total",
            ["limit"] = 10,
            ["offset"] = 0,
            ["referenceSources"] = false,
        }, cancellationToken: TestContext.Current.CancellationToken);

        read.IsError.Should().NotBe(true, Text(read));
        read.StructuredContent!.Value.GetProperty("result").GetProperty("rows").GetArrayLength().Should().Be(1);
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

    // The raw stream is the source of truth: a reader through the door gets the same documents, in the same order, as REST.
    [Fact]
    public async Task Documents_read_through_the_door_are_the_documents_the_rest_api_gives()
    {
        var host = Door();
        var type = await SeedAsync(host, 1, 2, 3);
        await using var client = await McpAsync(host, Key);
        var ct = TestContext.Current.CancellationToken;
        using var rest = Authorized(host);

        var read = await client.CallToolAsync("list_documents", new Dictionary<string, object?> { ["type"] = type }, cancellationToken: ct);
        var viaRest = System.Text.Json.JsonElement.Parse(await rest.GetStringAsync($"/formtypes/{type}/documents", ct));

        read.IsError.Should().NotBe(true, Text(read));
        var result = read.StructuredContent!.Value.GetProperty("result");
        result.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("documentId").GetGuid())
            .Should().Equal(viaRest.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("documentId").GetGuid()));
        result.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("body").GetProperty("total").GetInt64()).Should().Equal(1L, 2L, 3L);
        result.GetProperty("rawHead").GetInt64().Should().Be(viaRest.GetProperty("rawHead").GetInt64());
    }

    // A window the door cuts says so by its head: a reader continues from the last watermark until it reaches rawHead.
    [Fact]
    public async Task A_stream_is_paged_by_watermark_and_capped_by_the_door()
    {
        var host = Door(maxRows: 2);
        var type = await SeedAsync(host, 1, 2, 3);
        await using var client = await McpAsync(host, Key);
        var ct = TestContext.Current.CancellationToken;

        var first = (await client.CallToolAsync("list_documents", new Dictionary<string, object?> { ["type"] = type, ["limit"] = 50 }, cancellationToken: ct)).StructuredContent!.Value;
        var documents = first.GetProperty("result").GetProperty("documents").EnumerateArray().ToList();
        var lastSeen = documents[^1].GetProperty("watermark").GetInt64();
        var second = (await client.CallToolAsync("list_documents", new Dictionary<string, object?> { ["type"] = type, ["after"] = lastSeen }, cancellationToken: ct)).StructuredContent!.Value;
        var rest = second.GetProperty("result").GetProperty("documents").EnumerateArray().ToList();

        first.GetProperty("limit").GetInt32().Should().Be(2);
        documents.Should().HaveCount(2);
        lastSeen.Should().BeLessThan(first.GetProperty("result").GetProperty("rawHead").GetInt64());
        rest.Should().HaveCount(1);
        rest[0].GetProperty("watermark").GetInt64().Should().Be(second.GetProperty("result").GetProperty("rawHead").GetInt64());
        rest[0].GetProperty("body").GetProperty("total").GetInt64().Should().Be(3);
    }

    // Documents are kept before anything is declared or projected from them; the door must not hide them behind a projection.
    [Fact]
    public async Task The_raw_stream_is_readable_before_anything_is_declared_or_projected()
    {
        var host = Door();
        var type = $"mcp{Guid.NewGuid():N}"[..14];
        using var rest = Authorized(host);
        var accepted = await rest.PostAsync($"/formtypes/{type}/documents", new StringContent("""{"undeclared_field":"kept"}""", Encoding.UTF8, "application/json"), TestContext.Current.CancellationToken);
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        await using var client = await McpAsync(host, Key);
        var ct = TestContext.Current.CancellationToken;

        var listed = await client.CallToolAsync("list_documents", new Dictionary<string, object?> { ["type"] = type }, cancellationToken: ct);
        var document = listed.StructuredContent!.Value.GetProperty("result").GetProperty("documents").EnumerateArray().Single();
        var byId = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["id"] = document.GetProperty("documentId").GetGuid().ToString() }, cancellationToken: ct);

        document.GetProperty("body").GetProperty("undeclared_field").GetString().Should().Be("kept");
        byId.IsError.Should().NotBe(true, Text(byId));
        Text(byId).Should().Contain("undeclared_field").And.Contain(type);
    }

    [Fact]
    public async Task What_the_rest_api_refuses_about_documents_is_the_tools_error_in_the_same_words()
    {
        var host = Door();
        await using var client = await McpAsync(host, Key);
        var ct = TestContext.Current.CancellationToken;

        var unknown = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["id"] = Guid.NewGuid().ToString() }, cancellationToken: ct);
        var notAnId = await client.CallToolAsync("get_document", new Dictionary<string, object?> { ["id"] = "nope" }, cancellationToken: ct);
        var negative = await client.CallToolAsync("list_documents", new Dictionary<string, object?> { ["type"] = "anything", ["after"] = -1 }, cancellationToken: ct);

        unknown.IsError.Should().BeTrue();
        Text(unknown).Should().Contain("No document with id");
        notAnId.IsError.Should().BeTrue();
        Text(notAnId).Should().Contain("GUID");
        negative.IsError.Should().BeTrue();
        Text(negative).Should().Contain("cannot be negative");
    }
}
