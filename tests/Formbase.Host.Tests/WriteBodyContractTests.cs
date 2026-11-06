using System.Text;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// What a caller sends to a route that writes is theirs to get right, and getting it wrong is answered
/// with something they can act on: a 4xx with a body that says what was wrong — never a 5xx, which tells them
/// to wait for someone else, and never a 2xx for a body that is not what the route takes.
/// <para>
/// The routes that take a body (intake of a document, declaring a form type) are probed with the bodies a
/// client that is wrong in a different way would send: nothing at all, text that is not JSON, JSON of the
/// wrong shape, and JSON under the wrong content type. Intake takes any JSON value — a document's body is
/// opaque and stored verbatim (<c>DocumentBody</c>) — so for it only what is not JSON is wrong; a declaration
/// is an object, so every other value is.
/// </para>
/// </summary>
public sealed class WriteBodyContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public WriteBodyContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    private const string Intake = "/formtypes/inspection_log/documents";
    private const string Declaration = "/formtypes/inspection_log/declaration";

    /// <summary>A body that is not JSON at all: wrong for every route that reads one.</summary>
    private static readonly (string Name, string? Body, string ContentType)[] NotJson =
    [
        ("no body at all", null, "application/json"),
        ("an empty body", "", "application/json"),
        ("text that is not JSON", "this is not json", "application/json"),
        ("an unfinished object", "{\"a\":", "application/json"),
    ];

    /// <summary>JSON that is not an object: wrong for a route that takes a declaration, and for the route that takes a document.</summary>
    private static readonly (string Name, string? Body, string ContentType)[] JsonThatIsNotAnObject =
    [
        ("an array where an object is taken", "[]", "application/json"),
        ("a bare number", "42", "application/json"),
        ("a bare string", "\"text\"", "application/json"),
        ("null", "null", "application/json"),
    ];

    private async Task<List<string>> ProbeAsync(HttpMethod method, string route, IEnumerable<(string Name, string? Body, string ContentType)> bodies)
    {
        var failures = new List<string>();
        foreach (var (name, body, contentType) in bodies)
        {
            using var request = new HttpRequestMessage(method, route);
            if (body is not null)
            {
                request.Content = new StringContent(body, Encoding.UTF8, contentType);
            }

            using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
            var said = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
            var status = (int)response.StatusCode;

            if (status is < 400 or >= 500)
                failures.Add($"{name}: answered {status} — {Trim(said)}");
            else if (string.IsNullOrWhiteSpace(said))
                failures.Add($"{name}: answered {status} with nothing to read");
        }

        return failures;
    }

    [Fact]
    public async Task A_document_that_is_not_a_JSON_object_is_refused_and_says_so()
    {
        var failures = await ProbeAsync(HttpMethod.Post, Intake, NotJson.Concat(JsonThatIsNotAnObject));

        failures.Should().BeEmpty($"POST {Intake}:\n" + string.Join("\n", failures));
    }

    [Fact]
    public async Task A_declaration_that_is_not_a_JSON_object_is_refused_and_says_so()
    {
        var failures = await ProbeAsync(HttpMethod.Put, Declaration, NotJson.Concat(JsonThatIsNotAnObject));

        failures.Should().BeEmpty($"PUT {Declaration}:\n" + string.Join("\n", failures));
    }

    /// <summary>
    /// The other side of the same stance: what intake takes it stores as it was sent. A document is a set of fields, so
    /// only an object is one (an array posted to this route is the usual mistake — a list of documents sent to the
    /// route for one), but what an object holds is opaque and is not interpreted.
    /// </summary>
    [Fact]
    public async Task Intake_stores_an_object_it_is_sent_whatever_it_holds()
    {
        using var response = await _client.PostAsync(
            Intake,
            new StringContent("""{"a":[1,{"b":null}],"c":{"d":"e"},"f":"g"}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(201, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static string Trim(string said) => said.Length <= 160 ? said : said[..160] + "…";
}
