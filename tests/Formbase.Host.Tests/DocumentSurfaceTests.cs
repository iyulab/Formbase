using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// The two operations the surface can promise unconditionally: a document is accepted without a
/// declaration, and it can be read back whatever the projection is doing.
/// <para>
/// They run against the host as it is built, not against the engine behind it. The engine's
/// idempotency has its own tests; what is untested until here is whether the HTTP surface carries
/// it — a key read from the wrong place, or dropped, leaves the engine correct and the caller
/// duplicating documents on every retry.
/// </para>
/// </summary>
public sealed class DocumentSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DocumentSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_document_is_accepted_without_any_declaration()
    {
        var response = await PostAsync("orders", """{"customer":"ada","total":42}""");

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());

        var accepted = await ReadAsync(response);
        accepted.GetProperty("documentId").GetGuid().Should().NotBeEmpty();
        accepted.GetProperty("formType").GetString().Should().Be("orders");
        response.Headers.Location!.ToString().Should()
            .Be($"/documents/{accepted.GetProperty("documentId").GetGuid()}");
    }

    [Fact]
    public async Task An_accepted_document_reads_back_with_its_body_unchanged()
    {
        const string Body = """{"customer":"ada","total":42,"tags":["urgent"],"note":null}""";

        var accepted = await ReadAsync(await PostAsync("orders", Body));
        var id = accepted.GetProperty("documentId").GetGuid();

        var read = await _client.GetAsync($"/documents/{id}");
        read.StatusCode.Should().Be(HttpStatusCode.OK);

        var document = await ReadAsync(read);
        document.GetProperty("documentId").GetGuid().Should().Be(id);
        document.GetProperty("formType").GetString().Should().Be("orders");
        document.GetProperty("watermark").GetInt64().Should().BeGreaterThan(0);
        document.GetProperty("body").GetRawText().Should().Be(Body,
            "the raw store keeps what was sent — a body that comes back reshaped is a different " +
            "document from the one the caller submitted");
    }

    /// <summary>
    /// The retry a caller actually makes: the first response was lost, so the same request goes out
    /// again with the same key. It must land on the same document rather than a second copy — and
    /// the reply must not say which attempt this was, or the caller learns to distinguish them.
    /// </summary>
    [Fact]
    public async Task Re_submitting_with_the_same_key_returns_the_same_document()
    {
        var key = Guid.NewGuid();

        var first = await ReadAsync(await PostAsync("orders", """{"total":1}""", key));
        var second = await ReadAsync(await PostAsync("orders", """{"total":1}""", key));

        second.GetProperty("documentId").GetGuid().Should().Be(first.GetProperty("documentId").GetGuid());

        // The key is the identity, so the document is addressable by it directly — that equality is
        // what makes a retry safe to issue without first asking what the previous attempt produced.
        var stored = await ReadAsync(await _client.GetAsync($"/documents/{key}"));
        stored.GetProperty("documentId").GetGuid().Should().Be(key);
        stored.GetProperty("body").GetRawText().Should().Be("""{"total":1}""");
    }

    /// <summary>
    /// The half of idempotency that is easy to lose: the second submission must not consume a
    /// position in the stream. A store that appended a duplicate would still answer with one
    /// document by id, so identity alone does not show it — the watermark does.
    /// </summary>
    [Fact]
    public async Task A_repeated_submission_does_not_advance_the_stream()
    {
        var key = Guid.NewGuid();
        await PostAsync("orders", """{"total":1}""", key);
        var before = (await ReadAsync(await _client.GetAsync($"/documents/{key}")))
            .GetProperty("watermark").GetInt64();

        await PostAsync("orders", """{"total":1}""", key);
        var after = (await ReadAsync(await _client.GetAsync($"/documents/{key}")))
            .GetProperty("watermark").GetInt64();

        after.Should().Be(before, "a retry is not a new document, so it takes no new position");
    }

    /// <summary>
    /// Two submissions without a key are two documents. Without this, a surface that quietly reused
    /// one identity would pass every test above.
    /// </summary>
    [Fact]
    public async Task Submissions_without_a_key_are_separate_documents()
    {
        var first = await ReadAsync(await PostAsync("orders", """{"total":1}"""));
        var second = await ReadAsync(await PostAsync("orders", """{"total":1}"""));

        second.GetProperty("documentId").GetGuid().Should()
            .NotBe(first.GetProperty("documentId").GetGuid());
    }

    [Fact]
    public async Task A_key_that_cannot_be_an_identity_is_refused()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/formtypes/orders/documents")
        {
            Content = new StringContent("""{"total":1}""", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("Idempotency-Key", "not-a-uuid");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a key the store cannot use as an identity would stop deduplicating without saying so");
        (await ReadAsync(response)).GetProperty("detail").GetString()
            .Should().Contain("Idempotency-Key");
    }

    [Fact]
    public async Task A_body_that_is_not_json_is_refused()
    {
        var response = await PostAsync("orders", "not json at all");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(response)).GetProperty("detail").GetString().Should().Contain("JSON");
    }

    [Fact]
    public async Task An_unknown_document_is_a_404_rather_than_an_empty_reply()
    {
        var response = await _client.GetAsync($"/documents/{Guid.NewGuid()}");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(response)).GetProperty("status").GetInt32().Should().Be(404);
    }

    /// <summary>
    /// The surface is meant to be described, not only served — an endpoint absent from the document
    /// is invisible to a consumer generating a client from it.
    /// </summary>
    [Fact]
    public async Task The_openapi_document_describes_both_operations()
    {
        var response = await _client.GetAsync("/openapi/v1.json");
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var paths = (await ReadAsync(response)).GetProperty("paths");
        paths.TryGetProperty("/formtypes/{type}/documents", out _).Should().BeTrue();
        paths.TryGetProperty("/documents/{id}", out _).Should().BeTrue();
    }

    private Task<HttpResponseMessage> PostAsync(string type, string body, Guid? idempotencyKey = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/formtypes/{type}/documents")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

        if (idempotencyKey is not null)
        {
            request.Headers.Add("Idempotency-Key", idempotencyKey.Value.ToString());
        }

        return _client.SendAsync(request);
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>());
}
