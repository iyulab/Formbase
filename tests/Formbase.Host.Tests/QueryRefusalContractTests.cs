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
/// Where a query stops being a query. The document says a filter or ordering key that cannot be read
/// is refused rather than dropped, and the line it draws is between the question and the answer: a
/// column the declaration does not have is a question that cannot be asked, while a value that does
/// not fit the column it names is a question that can be asked and answers zero rows.
/// <para>
/// Both used to read the same over the wire — <c>200</c> with an empty page — which tells the caller
/// their data does not match when the truth is that their request was never understood. The two
/// cases need different remedies, so they need different replies.
/// </para>
/// </summary>
public sealed class QueryRefusalContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public QueryRefusalContractTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_filter_on_a_column_the_declaration_does_not_have_is_a_question_that_cannot_be_asked()
    {
        var type = await SeedProjectedAsync();

        var response = await _client.GetAsync($"/formtypes/{type}/records?filter=totl:1");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a mistyped column name is not a narrower question — it is one the projection has no " +
            "way to answer, and an empty page would send the caller looking at their data");
        (await ReadAsync(response)).GetProperty("type").GetString().Should().Be("/problems/invalid-query");
    }

    [Fact]
    public async Task An_ordering_key_the_declaration_does_not_have_is_refused_rather_than_ignored()
    {
        var type = await SeedProjectedAsync();

        var response = await _client.GetAsync($"/formtypes/{type}/records?orderBy=-totl");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "silently unordered rows look exactly like ordered ones until the page the caller " +
            "reads second disagrees with the page they read first");
        (await ReadAsync(response)).GetProperty("type").GetString().Should().Be("/problems/invalid-query");
    }

    /// <summary>
    /// The other side of the line, asserted so the split cannot quietly collapse in either direction.
    /// </summary>
    [Fact]
    public async Task A_value_that_does_not_fit_its_column_is_a_question_with_no_answer_not_a_bad_question()
    {
        var type = await SeedProjectedAsync();

        var response = await _client.GetAsync($"/formtypes/{type}/records?filter=total:not-a-number");

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "the column exists and the comparison is meaningful — no row holds that value, which " +
            "is an ordinary result and not a malformed request");
        (await ReadAsync(response)).GetProperty("rows").GetArrayLength().Should().Be(0);
    }

    /// <summary>
    /// A projection's bookkeeping columns are not in the row contract, so they are not orderable
    /// either — a caller who could sort by one would be reading a name the rows never carry.
    /// </summary>
    [Fact]
    public async Task The_projections_own_bookkeeping_is_not_a_column_a_caller_can_order_by()
    {
        var type = await SeedProjectedAsync();

        var response = await _client.GetAsync($"/formtypes/{type}/records?orderBy=fb_watermark");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ReadAsync(response)).GetProperty("type").GetString().Should().Be("/problems/invalid-query");
    }

    /// <summary>
    /// A value the surface cannot bind at all — the failure that happens before any endpoint code
    /// runs. The document tells callers to branch on <c>type</c>, and a reply from the framework's
    /// own handler carries one that is not in the table, so the instruction stops working exactly
    /// where the caller most needs it.
    /// </summary>
    [Fact]
    public async Task A_query_value_the_surface_cannot_bind_answers_from_the_documented_table()
    {
        var type = await SeedProjectedAsync();

        var response = await _client.GetAsync($"/formtypes/{type}/records?limit=every-single-one");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var problem = await ReadAsync(response);
        problem.GetProperty("type").GetString().Should().Be("/problems/invalid-request",
            "a caller branching on type has no case for a reply the table does not list, and a " +
            "value that never reached an endpoint is unreadable rather than an unanswerable query");
    }

    private async Task<string> SeedProjectedAsync()
    {
        var type = $"qr{Guid.NewGuid():N}"[..16];
        _factory.Services.GetRequiredService<InMemoryFieldHintSource>()
            .Declare(new FormTypeHints(FormTypeRef.Create(type), type, [new FieldHint("total", ColumnType.Integer)]));

        using var accepted = await _client.PostAsync(
            $"/formtypes/{type}/documents",
            new StringContent("""{"total":1}""", Encoding.UTF8, "application/json"));
        accepted.StatusCode.Should().Be(HttpStatusCode.Created, await accepted.Content.ReadAsStringAsync());

        using var projected = await _client.PostAsync($"/formtypes/{type}/projection", null);
        projected.StatusCode.Should().Be(HttpStatusCode.OK, await projected.Content.ReadAsStringAsync());

        return type;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty("every reply must carry something the caller can read");
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
