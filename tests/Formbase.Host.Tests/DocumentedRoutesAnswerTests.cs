using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// <see cref="DocsSurfaceParityTests"/> holds that every documented route is served; this holds that
/// every documented read route <em>answers</em> — a route can exist in the OpenAPI document and still
/// fail every call, and existence was all anything checked.
/// <para>
/// Each documented <c>GET</c> is called twice: over a form type with a declaration, a document and a
/// projection, and over one that has none of them. Either answer may be a refusal the page
/// describes — a <c>404</c>, a <c>409</c> — but not an unhandled failure. Write routes need a body
/// shaped to each one, and each has its own surface tests.
/// </para>
/// </summary>
public sealed class DocumentedRoutesAnswerTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DocumentedRoutesAnswerTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Every_documented_read_route_answers_without_an_unhandled_failure()
    {
        var ct = TestContext.Current.CancellationToken;
        var type = $"answers{Guid.NewGuid():N}"[..20];
        (await _client.PutAsJsonAsync($"/formtypes/{type}/declaration", new
        {
            tableName = type,
            declarationVersion = 1,
            fields = new[] { new { name = "n", type = "integer", nullable = true } }
        }, ct)).StatusCode.Should().Be(HttpStatusCode.Created);
        var accepted = await _client.PostAsJsonAsync($"/formtypes/{type}/documents?recordKey=r1", new { n = 1 }, ct);
        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        var documentId = JsonDocument.Parse(await accepted.Content.ReadAsStringAsync(ct)).RootElement.GetProperty("documentId").GetString()!;
        (await _client.PostAsync($"/formtypes/{type}/projection", null, ct)).StatusCode.Should().Be(HttpStatusCode.OK);

        var reads = DocsSurfaceParityTests.DocumentedRouteLines()
            .Where(r => r.Method == "GET")
            .Select(r => r.Path)
            .ToList();
        reads.Should().Contain("/formtypes/{type}/records", "the gate must reach the read routes it is for");

        var failures = new List<string>();
        foreach (var (formType, id) in new[] { (type, documentId), ($"absent{Guid.NewGuid():N}"[..20], Guid.NewGuid().ToString()) })
        {
            foreach (var route in reads)
            {
                var path = route.Replace("{type}", formType, StringComparison.Ordinal).Replace("{id}", id, StringComparison.Ordinal);
                var response = await _client.GetAsync(path, ct);
                if ((int)response.StatusCode >= 500)
                {
                    var body = await response.Content.ReadAsStringAsync(ct);
                    failures.Add($"GET {path} -> {(int)response.StatusCode} {body[..Math.Min(200, body.Length)]}");
                }
            }
        }

        // Joined so one run names every route that fails, not the first.
        string.Join(Environment.NewLine, failures).Should().BeEmpty(
            "a documented route that fails with a server error is a contract nobody can use");
    }
}
