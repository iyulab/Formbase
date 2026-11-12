using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// A repeated section has no documents of its own, so a reader that wants its rows reads them through the parent: each parent
/// document of a page with the rows it holds, cut the way the projection cuts them, and each row's citation name given rather
/// than left to be put together from a document id and a position.
/// </summary>
public sealed class SectionRowsEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public SectionRowsEndpointTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_section_answers_each_parent_document_with_its_rows_named_for_citation()
    {
        var (parent, section) = await DeclareAsync();
        var plain = await AcceptAsync(parent, """{"orderNo":"A-1","lines":[{"product":"bolt"},{"product":"nut"}]}""");
        var keyed = await AcceptAsync(parent, """{"orderNo":"A-2","lines":[{"product":"washer"}]}""", recordKey: "order-2");

        var page = await ReadRowsAsync(section);

        var documents = page.GetProperty("documents").EnumerateArray().ToList();
        documents.Select(d => d.GetProperty("documentId").GetGuid()).Should().Equal(plain, keyed);
        page.GetProperty("rawHead").GetInt64().Should().Be(documents[^1].GetProperty("watermark").GetInt64());

        var rows = documents[0].GetProperty("rows").EnumerateArray().ToList();
        rows.Select(r => r.GetProperty("name").GetString()).Should().Equal($"{plain}/0", $"{plain}/1");
        rows.Select(r => r.GetProperty("position").GetInt32()).Should().Equal(0, 1);
        rows[0].GetProperty("body").GetProperty("product").GetString().Should().Be("bolt");
        rows[0].GetProperty("body").GetProperty("orderNo").GetString().Should().Be("A-1", "each row carries the parent's key value");
        documents[0].GetProperty("refusal").ValueKind.Should().Be(JsonValueKind.Null);
    }

    /// <summary>
    /// The citation names the document a row was cut from; the record identity is the projection's name and, for a keyed parent,
    /// is the key — the same across the record's corrections. The two are told apart rather than one standing for both.
    /// </summary>
    [Fact]
    public async Task A_keyed_parents_rows_are_cited_by_document_and_identified_by_record()
    {
        var (parent, section) = await DeclareAsync();
        var first = await AcceptAsync(parent, """{"orderNo":"A-2","lines":[{"product":"washer"}]}""", recordKey: "order-2");
        var corrected = await AcceptAsync(parent, """{"orderNo":"A-2","lines":[{"product":"washer"},{"product":"gasket"}]}""", recordKey: "order-2");

        var documents = (await ReadRowsAsync(section)).GetProperty("documents").EnumerateArray().ToList();

        var citations = documents.SelectMany(d => d.GetProperty("rows").EnumerateArray()).Select(r => r.GetProperty("name").GetString());
        citations.Should().Equal($"{first}/0", $"{corrected}/0", $"{corrected}/1");
        var records = documents.SelectMany(d => d.GetProperty("rows").EnumerateArray()).Select(r => r.GetProperty("record").GetString());
        records.Should().Equal("order-2/0", "order-2/0", "order-2/1");
        documents.Select(d => d.GetProperty("recordKey").GetString()).Should().OnlyContain(k => k == "order-2");
    }

    [Fact]
    public async Task A_document_whose_section_cannot_be_read_says_why_and_holds_no_rows_while_one_without_a_section_holds_none_quietly()
    {
        var (parent, section) = await DeclareAsync();
        await AcceptAsync(parent, """{"orderNo":"A-3","lines":[{"product":"nut"},null]}""");
        await AcceptAsync(parent, """{"orderNo":"A-4"}""");

        var documents = (await ReadRowsAsync(section)).GetProperty("documents").EnumerateArray().ToList();

        documents[0].GetProperty("rows").GetArrayLength().Should().Be(0);
        documents[0].GetProperty("refusal").GetString().Should().Contain("row 1").And.Contain("not an object");
        documents[1].GetProperty("rows").GetArrayLength().Should().Be(0);
        documents[1].GetProperty("refusal").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public async Task A_retirement_holds_no_rows_and_is_marked()
    {
        var (parent, section) = await DeclareAsync();
        await AcceptAsync(parent, """{"orderNo":"A-5","lines":[{"product":"nut"}]}""", recordKey: "order-5");
        using var retire = await _client.DeleteAsync($"/formtypes/{parent}/records?recordKey=order-5", TestContext.Current.CancellationToken);
        retire.StatusCode.Should().Be(HttpStatusCode.OK);

        var documents = (await ReadRowsAsync(section)).GetProperty("documents").EnumerateArray().ToList();

        documents.Should().HaveCount(2);
        documents[1].GetProperty("retired").GetBoolean().Should().BeTrue();
        documents[1].GetProperty("rows").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public async Task The_page_continues_after_a_watermark_like_the_stream_it_reads()
    {
        var (parent, section) = await DeclareAsync();
        var first = await AcceptAsync(parent, """{"orderNo":"A-6","lines":[{"product":"a"}]}""");
        var second = await AcceptAsync(parent, """{"orderNo":"A-7","lines":[{"product":"b"}]}""");

        var one = await ReadRowsAsync(section, "limit=1");
        var next = await ReadRowsAsync(section, $"after={one.GetProperty("documents")[0].GetProperty("watermark").GetInt64()}");

        one.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("documentId").GetGuid()).Should().Equal(first);
        next.GetProperty("documents").EnumerateArray().Select(d => d.GetProperty("documentId").GetGuid()).Should().Equal(second);
    }

    [Fact]
    public async Task A_form_type_that_is_not_a_section_has_no_rows_to_read()
    {
        using var response = await _client.GetAsync($"/formtypes/plain_{Guid.NewGuid():N}/rows", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("type").GetString().Should().Be("/problems/not-a-section");
    }

    [Theory]
    [InlineData("after=-1")]
    [InlineData("limit=1001")]
    [InlineData("limit=-1")]
    public async Task A_page_is_refused_a_negative_watermark_or_a_size_it_cannot_hold(string query)
    {
        var (_, section) = await DeclareAsync();

        using var response = await _client.GetAsync($"/formtypes/{section}/rows?{query}", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>A parent that reads its key field from a nested place still joins its rows to it.</summary>
    [Fact]
    public async Task A_parent_whose_key_field_is_read_from_a_json_pointer_still_gives_its_rows_the_key()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = $"orders_{suffix}";
        var section = $"lines_{suffix}";
        using (var declared = await _client.PutAsJsonAsync(
            $"/formtypes/{section}/declaration",
            new { tableName = $"t_{section}", declarationVersion = 1, fields = new[] { new { name = "orderNo", type = "text" }, new { name = "product", type = "text" } } },
            TestContext.Current.CancellationToken))
        {
            declared.IsSuccessStatusCode.Should().BeTrue(await declared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var declared = await _client.PutAsJsonAsync(
            $"/formtypes/{parent}/declaration",
            new
            {
                tableName = $"t_{parent}",
                declarationVersion = 1,
                fields = new[] { new { name = "orderNo", type = "text", sourceKey = "/header/orderNo" } },
                relations = new[] { new { name = "lines", kind = "child", target = section, keyField = "orderNo", sectionField = "lines" } },
            },
            TestContext.Current.CancellationToken))
        {
            declared.IsSuccessStatusCode.Should().BeTrue(await declared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        await AcceptAsync(parent, """{"header":{"orderNo":"A-9"},"lines":[{"product":"bolt"}]}""");

        var rows = (await ReadRowsAsync(section)).GetProperty("documents")[0].GetProperty("rows");

        rows[0].GetProperty("body").GetProperty("orderNo").GetString().Should().Be("A-9");
    }

    /// <summary>
    /// The key field is written into each row under its key, so a section that reads it from a JSON Pointer can never be read. It
    /// is refused when declared — accepted, it made every later read of the section answer 400.
    /// </summary>
    [Fact]
    public async Task A_section_whose_key_field_is_read_from_a_json_pointer_is_refused_when_declared_after_its_parent()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = $"orders_{suffix}";
        var section = $"lines_{suffix}";
        using (var declared = await _client.PutAsJsonAsync(
            $"/formtypes/{parent}/declaration",
            new
            {
                tableName = $"t_{parent}",
                declarationVersion = 1,
                fields = new[] { new { name = "orderNo", type = "text" } },
                relations = new[] { new { name = "lines", kind = "child", target = section, keyField = "orderNo", sectionField = "lines" } },
            },
            TestContext.Current.CancellationToken))
        {
            declared.IsSuccessStatusCode.Should().BeTrue(await declared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using var response = await _client.PutAsJsonAsync(
            $"/formtypes/{section}/declaration",
            new
            {
                tableName = $"t_{section}",
                declarationVersion = 1,
                fields = new object[] { new { name = "orderNo", type = "text", sourceKey = "/link/orderNo" }, new { name = "product", type = "text" } },
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("JSON Pointer");
        using var stored = await _client.GetAsync($"/formtypes/{section}/declaration", TestContext.Current.CancellationToken);
        stored.StatusCode.Should().Be(HttpStatusCode.NotFound, "a refused declaration stores nothing");
    }

    [Fact]
    public async Task A_parent_naming_a_section_whose_key_field_is_read_from_a_json_pointer_is_refused()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = $"orders_{suffix}";
        var section = $"lines_{suffix}";
        using (var declared = await _client.PutAsJsonAsync(
            $"/formtypes/{section}/declaration",
            new
            {
                tableName = $"t_{section}",
                declarationVersion = 1,
                fields = new object[] { new { name = "orderNo", type = "text", sourceKey = "/link/orderNo" }, new { name = "product", type = "text" } },
            },
            TestContext.Current.CancellationToken))
        {
            declared.IsSuccessStatusCode.Should().BeTrue(await declared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using var response = await _client.PutAsJsonAsync(
            $"/formtypes/{parent}/declaration",
            new
            {
                tableName = $"t_{parent}",
                declarationVersion = 1,
                fields = new[] { new { name = "orderNo", type = "text" } },
                relations = new[] { new { name = "lines", kind = "child", target = section, keyField = "orderNo", sectionField = "lines" } },
            },
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().Contain("JSON Pointer");
        using var stored = await _client.GetAsync($"/formtypes/{parent}/declaration", TestContext.Current.CancellationToken);
        stored.StatusCode.Should().Be(HttpStatusCode.NotFound, "a refused declaration stores nothing");
    }

    private async Task<(string Parent, string Section)> DeclareAsync()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var parent = $"orders_{suffix}";
        var section = $"lines_{suffix}";
        using (var declared = await _client.PutAsJsonAsync(
            $"/formtypes/{section}/declaration",
            new { tableName = $"t_{section}", declarationVersion = 1, fields = new[] { new { name = "orderNo", type = "text" }, new { name = "product", type = "text" } } },
            TestContext.Current.CancellationToken))
        {
            declared.IsSuccessStatusCode.Should().BeTrue(await declared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        using (var declared = await _client.PutAsJsonAsync(
            $"/formtypes/{parent}/declaration",
            new
            {
                tableName = $"t_{parent}",
                declarationVersion = 1,
                fields = new[] { new { name = "orderNo", type = "text" } },
                relations = new[] { new { name = "lines", kind = "child", target = section, keyField = "orderNo", sectionField = "lines" } },
            },
            TestContext.Current.CancellationToken))
        {
            declared.IsSuccessStatusCode.Should().BeTrue(await declared.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        }

        return (parent, section);
    }

    private async Task<Guid> AcceptAsync(string type, string body, string? recordKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/formtypes/{type}/documents{(recordKey is null ? "" : $"?recordKey={recordKey}")}")
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };
        using var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken)).GetProperty("documentId").GetGuid();
    }

    private async Task<JsonElement> ReadRowsAsync(string section, string query = "")
    {
        using var response = await _client.GetAsync($"/formtypes/{section}/rows{(query.Length == 0 ? "" : "?" + query)}", TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }
}
