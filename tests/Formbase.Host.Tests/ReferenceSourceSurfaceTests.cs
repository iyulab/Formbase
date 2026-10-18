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
/// Over HTTP, a reference field's source is asked for with <c>referenceSources=true</c> and arrives
/// beside the row's fields; a query that does not ask carries no <c>references</c> at all.
/// </summary>
public sealed class ReferenceSourceSurfaceTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ReferenceSourceSurfaceTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task A_reference_field_names_the_record_its_value_was_read_from_when_asked()
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var customers = "cust" + suffix;
        var orders = "ord" + suffix;
        var hints = _factory.Services.GetRequiredService<InMemoryFieldHintSource>();
        hints.Declare(new FormTypeHints(FormTypeRef.Create(customers), customers,
        [
            new FieldHint("code", ColumnType.Text),
            new FieldHint("grade", ColumnType.Text),
        ]));
        hints.Declare(new FormTypeHints(FormTypeRef.Create(orders), orders,
        [
            new FieldHint("customer_ref", ColumnType.Text),
            new FieldHint("qty", ColumnType.Integer),
            new FieldHint("grade_now", ColumnType.Text, Binding: FieldBinding.Reference,
                Target: new EntityRef(FormTypeRef.Create(customers), "grade", TargetLookup.Field("code"), "customer_ref")),
        ]));

        var customer = await AcceptAsync(customers, """{"code":"C-1","grade":"A"}""", "cust-1");
        await AcceptAsync(orders, """{"customer_ref":"C-1","qty":1}""");
        await AcceptAsync(orders, """{"customer_ref":"C-9","qty":2}""");
        await ProjectAsync(customers);
        await ProjectAsync(orders);

        var rows = await ReadRowsAsync($"/formtypes/{orders}/records?orderBy=qty&referenceSources=true");

        var found = rows[0].GetProperty("references").GetProperty("grade_now");
        found.GetProperty("outcome").GetString().Should().Be("resolved");
        found.GetProperty("target").GetProperty("document").GetGuid().Should().Be(customer);
        found.GetProperty("target").GetProperty("key").GetString().Should().Be("cust-1");

        var missing = rows[1].GetProperty("references").GetProperty("grade_now");
        missing.GetProperty("outcome").GetString().Should().Be("noTarget");
        missing.GetProperty("target").ValueKind.Should().Be(JsonValueKind.Null);

        (await ReadRowsAsync($"/formtypes/{orders}/records")).Should().AllSatisfy(
            row => row.TryGetProperty("references", out _).Should().BeFalse("only a caller that asks pays for the sources"));
    }

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

    private async Task ProjectAsync(string type)
    {
        var projected = await _client.PostAsync($"/formtypes/{type}/projection", null, TestContext.Current.CancellationToken);
        projected.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<List<JsonElement>> ReadRowsAsync(string path)
    {
        var result = await ReadAsync(await _client.GetAsync(path, TestContext.Current.CancellationToken));
        return [.. result.GetProperty("rows").EnumerateArray()];
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        var payload = await response.Content.ReadAsStringAsync();
        payload.Should().NotBeEmpty("every reply must carry something the caller can read");
        return JsonDocument.Parse(payload).RootElement.Clone();
    }
}
