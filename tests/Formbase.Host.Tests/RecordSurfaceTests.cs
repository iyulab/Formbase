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
/// Reading projected records over HTTP, and the refusals that are not empty results.
/// <para>
/// The query crosses as text: a filter value arrives as a string whatever the column holds, so what
/// is really under test is that the surface hands the engine something it can compare — a filter
/// that silently failed to match would look exactly like a query with no results.
/// </para>
/// </summary>
public sealed class RecordSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public RecordSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_form_type_that_was_never_projected_refuses_rather_than_reading_empty()
    {
        var type = NewFormType();
        await AcceptAsync(type, """{"total":1}""");

        var response = await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict,
            "'nothing has been built' and 'nothing matched' call for different remedies, and an " +
            "empty page would tell the caller the wrong one");
        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().Should().Be("/problems/not-projected");
        problem.GetProperty("detail").GetString().Should().Contain("Declare field hints first")
            .And.NotContain("trigger a projection",
                "with nothing declared a projection run projects nothing — offering it as a remedy " +
                "sends the caller round a loop the server never names");
    }

    [Fact]
    public async Task A_declared_form_type_not_yet_projected_names_projection_as_the_remedy()
    {
        var type = NewFormType();
        Declare(type, ("total", ColumnType.Integer));
        await AcceptAsync(type, """{"total":1}""");

        var response = await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().Should().Be("/problems/not-projected");
        problem.GetProperty("detail").GetString().Should().Contain("trigger a projection")
            .And.NotContain("Declare field hints");
    }

    [Fact]
    public async Task Projected_records_read_back_with_their_declared_columns()
    {
        var type = await SeedProjectedAsync(("total", 1), ("total", 2));

        var result = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken));

        result.GetProperty("stale").GetBoolean().Should().BeFalse();
        var rows = result.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(2);
        rows.Select(r => r.GetProperty("total").GetInt64()).Should().BeEquivalentTo([1L, 2L]);
        rows[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["total"],
            "a row carries the declared columns and nothing else — the projection's own bookkeeping " +
            "would calcify into the caller's contract");
    }

    /// <summary>
    /// The filter value is a string on the wire and an integer in the column. Nothing about a
    /// mismatched comparison is visible in the reply — it returns rows, just the wrong ones — so
    /// this asserts the selection, not merely that a request succeeded.
    /// </summary>
    [Fact]
    public async Task A_filter_value_is_compared_as_the_declared_column_type()
    {
        var type = await SeedProjectedAsync(("total", 1), ("total", 2), ("total", 3));

        var result = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records?filter=total:2", TestContext.Current.CancellationToken));

        var rows = result.GetProperty("rows").EnumerateArray().ToList();
        rows.Should().HaveCount(1, "a filter that failed to compare would return every row instead");
        rows[0].GetProperty("total").GetInt64().Should().Be(2);
    }

    [Fact]
    public async Task Ordering_and_paging_select_a_deterministic_slice()
    {
        var type = await SeedProjectedAsync(("total", 1), ("total", 2), ("total", 3));

        var result = await ReadAsync(
            await _client.GetAsync($"/formtypes/{type}/records?orderBy=-total&limit=2", TestContext.Current.CancellationToken));

        result.GetProperty("rows").EnumerateArray()
            .Select(r => r.GetProperty("total").GetInt64())
            .Should().Equal(3L, 2L);
    }

    [Fact]
    public async Task Records_read_after_a_new_document_are_served_and_flagged_stale()
    {
        var type = await SeedProjectedAsync(("total", 1));
        await AcceptAsync(type, """{"total":2}""");

        var result = await ReadAsync(await _client.GetAsync($"/formtypes/{type}/records", TestContext.Current.CancellationToken));

        result.GetProperty("stale").GetBoolean().Should().BeTrue(
            "the caller can decide whether an answer from an earlier point in the stream is good " +
            "enough — but only if they are told");
        result.GetProperty("rows").GetArrayLength().Should().Be(1,
            "the rows are still served; staleness is a fact about them, not a refusal");
    }

    [Fact]
    public async Task A_filter_that_cannot_be_read_is_refused_rather_than_dropped()
    {
        var type = await SeedProjectedAsync(("total", 1));

        var response = await _client.GetAsync($"/formtypes/{type}/records?filter=total", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a dropped filter widens the result — the caller would read rows they asked to exclude");
        (await ReadAsync(response)).GetProperty("type").GetString()
            .Should().Be("/problems/invalid-query");
    }

    [Fact]
    public async Task A_filter_value_may_contain_the_separator()
    {
        var type = NewFormType();
        Declare(type, ("label", ColumnType.Text));
        await AcceptAsync(type, """{"label":"09:30"}""");
        await AcceptAsync(type, """{"label":"10:30"}""");
        await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);

        var result = await ReadAsync(
            await _client.GetAsync($"/formtypes/{type}/records?filter=label:09:30", TestContext.Current.CancellationToken));

        result.GetProperty("rows").GetArrayLength().Should().Be(1,
            "only the first colon separates column from value, so a value may hold more");
    }

    private static string NewFormType() => $"rec{Guid.NewGuid():N}"[..16];

    private void Declare(string type, params (string Name, ColumnType Type)[] fields) =>
        _factory.Services.GetRequiredService<InMemoryFieldHintSource>()
            .Declare(new FormTypeHints(
                FormTypeRef.Create(type),
                type,
                [.. fields.Select(f => new FieldHint(f.Name, f.Type))]));

    private async Task<string> SeedProjectedAsync(params (string Field, int Value)[] documents)
    {
        var type = NewFormType();
        Declare(type, (documents[0].Field, ColumnType.Integer));

        foreach (var (field, value) in documents)
        {
            await AcceptAsync(type, $$"""{"{{field}}":{{value}}}""");
        }

        var projected = await _client.PostAsync($"/formtypes/{type}/projection", null);
        projected.StatusCode.Should().Be(HttpStatusCode.OK);
        return type;
    }

    private async Task AcceptAsync(string type, string body)
    {
        var response = await _client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent(body, Encoding.UTF8, "application/json"));
        response.StatusCode.Should().Be(HttpStatusCode.Created,
            await response.Content.ReadAsStringAsync());
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty("every reply must carry something the caller can read");
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
