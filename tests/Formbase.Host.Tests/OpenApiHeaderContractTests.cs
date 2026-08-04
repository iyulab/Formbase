using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// The two headers this surface asks callers to send must be in the generated document.
/// <para>
/// Both are read outside an endpoint's signature — one from the request, one in middleware ahead of
/// routing — so nothing puts them in the document by itself. A consumer generating a client from it
/// would get one that cannot send an idempotency key or address a namespace: exactly the two
/// features whose value depends on callers using them from the first request rather than after a
/// rewrite.
/// </para>
/// <para>
/// The route parity gate cannot see this. It compares method and path, so parameters are outside
/// its reach — the same shape of gap, one level down.
/// </para>
/// </summary>
public sealed class OpenApiHeaderContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public OpenApiHeaderContractTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task The_namespace_selector_is_declared_on_every_operation()
    {
        var operations = await OperationsAsync();

        operations.Should().NotBeEmpty();
        operations.Where(o => !HeaderNames(o.Value).Contains("Formbase-Namespace"))
            .Select(o => o.Key)
            .Should().BeEmpty(
                "the selector applies to every request, so a client generated from the document " +
                "must be able to send it on every request");
    }

    [Fact]
    public async Task The_idempotency_key_is_declared_on_intake()
    {
        var operations = await OperationsAsync();

        var intake = operations
            .Single(o => o.Key.Contains("/documents", StringComparison.Ordinal)
                      && o.Key.StartsWith("POST", StringComparison.Ordinal));

        HeaderNames(intake.Value).Should().Contain("Idempotency-Key",
            "a generated client that cannot send the key cannot retry safely, which is the whole " +
            "reason the key exists");
    }

    private async Task<Dictionary<string, JsonElement>> OperationsAsync()
    {
        var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));

        return document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Select(method => (Key: $"{method.Name.ToUpperInvariant()} {path.Name}", method.Value)))
            .ToDictionary(o => o.Key, o => o.Value.Clone(), StringComparer.Ordinal);
    }

    private static IEnumerable<string?> HeaderNames(JsonElement operation) =>
        operation.TryGetProperty("parameters", out var parameters)
            ? parameters.EnumerateArray()
                .Where(p => p.GetProperty("in").GetString() == "header")
                .Select(p => p.GetProperty("name").GetString())
            : [];
}
