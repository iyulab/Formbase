using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// Holds what a caller is told when the host cannot read their request.
/// <para>
/// The engine's own refusals are written to guide — "The declaration must name the table its
/// projection builds." A body the deserializer chokes on took a different path: it never reached an
/// endpoint, so the framework answered, and its answer named the parameter and the CLR type it was
/// binding to. That is the first error a new consumer meets (a mistyped field type), and it told
/// them nothing they could act on while telling them something about this assembly's internals that
/// no public response should carry.
/// </para>
/// <para>
/// Two things are held here, because fixing one without the other leaves the gap open: the response
/// must name the offending field in the caller's own vocabulary (the JSON path of what they sent),
/// and no problem response may leak an internal identifier. The second is swept across a set of
/// wrong requests rather than asserted on one, since the leak came from a shared default rather
/// than from any one endpoint.
/// </para>
/// </summary>
public sealed class UnreadableRequestProblemTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public UnreadableRequestProblemTests(WebApplicationFactory<Program> factory)
        => _client = factory.CreateClient();

    /// <summary>
    /// Tokens that only mean something to someone reading this repository's source. A response
    /// carrying one is describing the host rather than the request -- and the language policy for
    /// published text scrubs exactly this class of token.
    /// </summary>
    private static readonly string[] InternalTokens =
    [
        "Formbase.", "System.", "Microsoft.", "Npgsql", "MorphDB.",
        "Dto", "request\" from the request body",
    ];

    private static StringContent Json(string body) => new(body, Encoding.UTF8, "application/json");

    public static TheoryData<string, HttpMethod, string> WrongRequests() => new()
    {
        // A value that is syntactically fine and semantically not one of the accepted field types --
        // the mistyped-enum case, which is the likeliest first mistake.
        {
            "/formtypes/orders/declaration", HttpMethod.Put,
            """{"tableName":"orders","declarationVersion":1,"fields":[{"name":"total","type":"stringy"}]}"""
        },
        // A number where an object belongs.
        {
            "/formtypes/orders/declaration", HttpMethod.Put,
            """{"tableName":"orders","declarationVersion":1,"fields":[7]}"""
        },
        // Not JSON at all.
        { "/formtypes/orders/declaration", HttpMethod.Put, "{" },
        // The same two failures on the intake surface, which binds a different shape.
        { "/formtypes/orders/documents", HttpMethod.Post, "{" },
        { "/formtypes/orders/documents", HttpMethod.Post, "[1,2,3" },
    };

    [Theory]
    [MemberData(nameof(WrongRequests))]
    public async Task No_problem_response_names_anything_internal(string route, HttpMethod method, string body)
    {
        using var request = new HttpRequestMessage(method, route) { Content = Json(body) };
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "an unreadable body is the caller's to fix, and a 5xx would tell them the host had broken instead");

        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("detail").GetString() ?? string.Empty;

        foreach (var token in InternalTokens)
        {
            detail.Should().NotContain(token,
                $"a problem response is read by a consumer who cannot see this source, and '{token}' "
                + "describes the host rather than what they sent");
        }
    }

    [Fact]
    public async Task A_value_the_shape_does_not_accept_is_named_by_where_it_sits_in_the_request()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/formtypes/orders/declaration")
        {
            Content = Json("""{"tableName":"orders","declarationVersion":1,"fields":[{"name":"total","type":"stringy"}]}"""),
        };
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("detail").GetString() ?? string.Empty;

        detail.Should().Contain("fields",
            "the caller has to know which of their fields to change, and the JSON path is the one "
            + "name for it they already have -- they wrote it");
        detail.Should().Contain("docs/API.md",
            "knowing the value is wrong does not say what would be right; the page that lists the "
            + "accepted types is the shortest way there");
    }

    [Fact]
    public async Task A_body_that_is_not_json_says_so_rather_than_naming_a_field()
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/formtypes/orders/declaration")
        {
            Content = Json("{"),
        };
        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        var detail = (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken))
            .GetProperty("detail").GetString() ?? string.Empty;

        detail.Should().Contain("not valid JSON",
            "there is no field to point at when the document never parsed, and pointing at one "
            + "would send the caller looking in the wrong place");
    }
}
