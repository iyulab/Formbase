using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// The operations the surface can promise unconditionally: a document is accepted without a
/// declaration, and it can be read back — by id, or in its form type's stream — whatever the
/// projection is doing.
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
            await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));

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

        var read = await _client.GetAsync($"/documents/{id}", TestContext.Current.CancellationToken);
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
        var stored = await ReadAsync(await _client.GetAsync($"/documents/{key}", TestContext.Current.CancellationToken));
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
        var before = (await ReadAsync(await _client.GetAsync($"/documents/{key}", TestContext.Current.CancellationToken)))
            .GetProperty("watermark").GetInt64();

        await PostAsync("orders", """{"total":1}""", key);
        var after = (await ReadAsync(await _client.GetAsync($"/documents/{key}", TestContext.Current.CancellationToken)))
            .GetProperty("watermark").GetInt64();

        after.Should().Be(before, "a retry is not a new document, so it takes no new position");
    }

    /// <summary>
    /// A key sent again under another form type is a second request, not a retry. Answering it as a
    /// retry named the new form type in a 201 while the document stayed under the first one and the new
    /// body went nowhere — so the refusal is checked together with both streams.
    /// </summary>
    [Fact]
    public async Task A_key_reused_for_another_form_type_is_refused_and_stores_nothing()
    {
        var key = Guid.NewGuid();
        var first = UniqueType();
        var second = UniqueType();
        (await PostAsync(first, """{"total":1}""", key)).StatusCode.Should().Be(HttpStatusCode.Created);

        using var reuse = await PostAsync(second, """{"other":"x"}""", key);

        reuse.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        (await ReadAsync(reuse)).GetProperty("type").GetString().Should().Be("/problems/idempotency-key-reused");
        var held = await ReadAsync(await _client.GetAsync($"/documents/{key}", TestContext.Current.CancellationToken));
        held.GetProperty("formType").GetString().Should().Be(first);
        held.GetProperty("body").GetProperty("total").GetInt32().Should().Be(1);
        var stream = await ReadAsync(await _client.GetAsync($"/formtypes/{second}/documents", TestContext.Current.CancellationToken));
        stream.GetProperty("documents").GetArrayLength().Should().Be(0);
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

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

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
        var response = await _client.GetAsync($"/documents/{Guid.NewGuid()}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ReadAsync(response)).GetProperty("status").GetInt32().Should().Be(404);
    }

    /// <summary>
    /// The reason the stream read exists: before anything is declared, the documents' fields are
    /// readable over HTTP without knowing any document's id. Records answer only for declared columns
    /// and a single read needs an id the caller received at intake, so without this a consumer on the
    /// other side of the container boundary could not see what has not been declared yet.
    /// </summary>
    [Fact]
    public async Task A_form_types_stream_is_readable_before_anything_is_declared()
    {
        var type = UniqueType();
        await PostAsync(type, """{"wo":"WO-1","started":"2024-03-11"}""");
        await PostAsync(type, """{"wo":"WO-2","tech":"kim"}""");
        await PostAsync(UniqueType(), """{"elsewhere":true}""");

        var page = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/documents", TestContext.Current.CancellationToken));

        var documents = page.GetProperty("documents").EnumerateArray().ToList();
        documents.Select(d => d.GetProperty("body").GetRawText()).Should().Equal(
            """{"wo":"WO-1","started":"2024-03-11"}""",
            """{"wo":"WO-2","tech":"kim"}""");
        documents.Should().OnlyContain(d => d.GetProperty("formType").GetString() == type,
            "a form type's stream carries its own documents and no other type's");
        page.GetProperty("rawHead").GetInt64().Should().Be(documents[^1].GetProperty("watermark").GetInt64(),
            "a page that reached the end of the stream ends at the head it reports");
    }

    /// <summary>
    /// Paging by watermark: each page continues after the last watermark the caller received, the
    /// pages together are the stream in order with nothing repeated or skipped, and a caller that has
    /// read to the head gets an empty page rather than an error.
    /// </summary>
    [Fact]
    public async Task The_stream_pages_by_watermark_until_the_caller_reaches_the_head()
    {
        var type = UniqueType();
        for (var i = 1; i <= 5; i++)
        {
            await PostAsync(type, $$"""{"n":{{i}}}""");
        }

        var seen = new List<string>();
        long after = 0;
        long head;
        while (true)
        {
            var page = await ReadAsync(await _client.GetAsync(
                $"/formtypes/{type}/documents?after={after}&limit=2", TestContext.Current.CancellationToken));
            head = page.GetProperty("rawHead").GetInt64();
            var documents = page.GetProperty("documents").EnumerateArray().ToList();
            documents.Count.Should().BeLessThanOrEqualTo(2);
            if (documents.Count == 0)
            {
                break;
            }

            seen.AddRange(documents.Select(d => d.GetProperty("body").GetRawText()));
            after = documents[^1].GetProperty("watermark").GetInt64();
        }

        seen.Should().Equal("""{"n":1}""", """{"n":2}""", """{"n":3}""", """{"n":4}""", """{"n":5}""");
        after.Should().Be(head, "the caller is caught up exactly when its cursor equals the head");
    }

    [Fact]
    public async Task A_zero_limit_reads_only_the_head()
    {
        var type = UniqueType();
        await PostAsync(type, """{"n":1}""");

        var page = await ReadAsync(await _client.GetAsync(
            $"/formtypes/{type}/documents?limit=0", TestContext.Current.CancellationToken));

        page.GetProperty("documents").GetArrayLength().Should().Be(0);
        page.GetProperty("rawHead").GetInt64().Should().BeGreaterThan(0,
            "the head is what a caller polling for new documents asks for, without paying for a page");
    }

    [Fact]
    public async Task A_form_type_with_no_documents_reads_an_empty_page_at_head_zero()
    {
        var page = await ReadAsync(await _client.GetAsync(
            $"/formtypes/{UniqueType()}/documents", TestContext.Current.CancellationToken));

        page.GetProperty("documents").GetArrayLength().Should().Be(0);
        page.GetProperty("rawHead").GetInt64().Should().Be(0);
    }

    [Theory]
    [InlineData("limit=-1")]
    [InlineData("limit=1001")]
    [InlineData("after=-1")]
    public async Task A_page_request_that_cannot_be_honoured_is_refused(string query)
    {
        var response = await _client.GetAsync(
            $"/formtypes/{UniqueType()}/documents?{query}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a limit clamped to what the server holds would read as the end of the stream");
        (await ReadAsync(response)).GetProperty("type").GetString().Should().Be("/problems/invalid-request");
    }

    /// <summary>
    /// The surface is meant to be described, not only served — an endpoint absent from the document
    /// is invisible to a consumer generating a client from it.
    /// </summary>
    [Fact]
    public async Task The_openapi_document_describes_the_document_operations()
    {
        var response = await _client.GetAsync("/openapi/v1.json", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK);

        var paths = (await ReadAsync(response)).GetProperty("paths");
        paths.TryGetProperty("/formtypes/{type}/documents", out var documents).Should().BeTrue();
        documents.TryGetProperty("post", out _).Should().BeTrue();
        documents.TryGetProperty("get", out _).Should().BeTrue();
        paths.TryGetProperty("/documents/{id}", out _).Should().BeTrue();
    }

    private static string UniqueType() => "stream-" + Guid.NewGuid().ToString("N");

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
