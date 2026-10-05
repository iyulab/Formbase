using System.Net;
using System.Text;
using System.Text.Json;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Host.Tests;

/// <summary>
/// Correcting and retiring a record over HTTP: a document names its record in the query, a retirement
/// is a DELETE on the records it leaves, and the raw stream says which appends were which.
/// </summary>
public sealed class RecordIdentitySurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RecordIdentitySurfaceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_corrected_record_reads_back_as_one_row_with_the_latest_values()
    {
        var type = NewFormType();
        Declare(type, ("total", ColumnType.Integer));
        await AcceptAsync(type, """{"total":1}""", "주문/1041");
        await AcceptAsync(type, """{"total":2}""", "주문/1041");
        await AcceptAsync(type, """{"total":7}""");

        var rows = await ProjectAndReadRowsAsync(type);

        rows.Should().HaveCount(2);
        rows.Select(r => r.GetProperty("fields").GetProperty("total").GetInt64()).Should().BeEquivalentTo([2L, 7L],
            "the key — non-ASCII, with a slash — arrived exactly as sent, or the two versions would be two rows");
    }

    [Fact]
    public async Task A_row_says_which_record_it_is_and_keeps_its_key_through_a_correction()
    {
        var type = NewFormType();
        Declare(type, ("total", ColumnType.Integer));
        var original = await AcceptAsync(type, """{"total":1}""", "주문/1041");
        var standalone = await AcceptAsync(type, """{"total":7}""");

        var before = await ProjectAndReadRowsAsync(type);

        before.Select(Identity).Should().Equal(
            [(original, "주문/1041"), (standalone, null)],
            "a row names the document it stands on, and its key when it was sent with one");
        before[1].GetProperty("record").GetProperty("key").ValueKind.Should().Be(JsonValueKind.Null,
            "a record of its own says so with an explicit null, not by leaving the key out");

        var corrected = await AcceptAsync(type, """{"total":2}""", "주문/1041");

        var after = await ProjectAndReadRowsAsync(type);

        after.Select(Identity).Should().Equal(
            [(standalone, null), (corrected, "주문/1041")],
            "the corrected record keeps its key and now stands on the correcting document");
        after[1].GetProperty("fields").GetProperty("total").GetInt64().Should().Be(2);
    }

    private static (Guid Document, string? Key) Identity(JsonElement row)
    {
        var record = row.GetProperty("record");
        return (record.GetProperty("document").GetGuid(), record.GetProperty("key").GetString());
    }

    [Fact]
    public async Task A_retired_record_leaves_the_records_and_stays_in_the_stream()
    {
        var type = NewFormType();
        Declare(type, ("total", ColumnType.Integer));
        await AcceptAsync(type, """{"total":1}""", "a");

        var retired = await _client.DeleteAsync($"/formtypes/{type}/records?recordKey=a", TestContext.Current.CancellationToken);

        retired.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadAsync(retired)).GetProperty("recordKey").GetString().Should().Be("a");
        (await ProjectAndReadRowsAsync(type)).Should().BeEmpty();

        var page = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/documents", TestContext.Current.CancellationToken));
        var documents = page.GetProperty("documents").EnumerateArray().ToList();
        documents.Should().HaveCount(2);
        documents[0].GetProperty("retired").GetBoolean().Should().BeFalse();
        documents[1].GetProperty("retired").GetBoolean().Should().BeTrue();
        documents[1].GetProperty("body").ValueKind.Should().Be(JsonValueKind.Null);
        documents[1].GetProperty("recordKey").GetString().Should().Be("a");
    }

    [Fact]
    public async Task A_blank_record_key_is_refused_rather_than_read_as_none()
    {
        var response = await _client.PostAsync(
            $"/formtypes/{NewFormType()}/documents?recordKey=%20",
            new StringContent("""{"total":1}""", Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Retiring_without_naming_the_record_is_refused()
    {
        var response = await _client.DeleteAsync($"/formtypes/{NewFormType()}/records", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static string NewFormType() => $"rid{Guid.NewGuid():N}"[..16];

    private void Declare(string type, params (string Name, ColumnType Type)[] fields) =>
        _factory.Services.GetRequiredService<InMemoryFieldHintSource>()
            .Declare(new FormTypeHints(
                FormTypeRef.Create(type),
                type,
                [.. fields.Select(f => new FieldHint(f.Name, f.Type))]));

    private async Task<Guid> AcceptAsync(string type, string body, string? recordKey = null)
    {
        var query = recordKey is null ? "" : $"?recordKey={Uri.EscapeDataString(recordKey)}";
        var response = await _client.PostAsync(
            $"/formtypes/{type}/documents{query}",
            new StringContent(body, Encoding.UTF8, "application/json"),
            TestContext.Current.CancellationToken);
        response.StatusCode.Should().Be(HttpStatusCode.Created, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return (await ReadAsync(response)).GetProperty("documentId").GetGuid();
    }

    private async Task<List<JsonElement>> ProjectAndReadRowsAsync(string type)
    {
        var projected = await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);
        projected.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken));
        return [.. result.GetProperty("rows").EnumerateArray()];
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty("every reply must carry something the caller can read");
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
