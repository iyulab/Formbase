using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// Holds <c>docs/API.md</c> to what the host actually serves. A route the documentation promises
/// and the host does not answer is not a cosmetic error: a consumer discovers it as a 404 inside
/// their own integration, having written code against a page that was wrong when they read it.
/// <para>
/// The served side is read from the generated OpenAPI document rather than a list kept by hand, so
/// it cannot fall out of date on its own — the thing this gate is protecting against is exactly a
/// hand-maintained inventory drifting from the code beside it.
/// </para>
/// <para>
/// Both directions are enforced here. Every documented route must exist, and every served route
/// must be documented: this surface is small and entirely public, so an endpoint missing from the
/// page is one no consumer will find rather than an internal deliberately left out.
/// </para>
/// </summary>
public sealed partial class DocsSurfaceParityTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public DocsSurfaceParityTests(WebApplicationFactory<Program> factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Every_documented_route_is_served()
    {
        var documented = DocumentedRoutes();
        var served = await ServedRoutesAsync();

        documented.Should().NotBeEmpty("the route block must exist for this gate to mean anything");

        // Joined rather than compared as sets so one failure names every drift at once — fixing
        // them one run at a time is how a sweep misses instances.
        string.Join(", ", documented.Except(served).Order(StringComparer.Ordinal)).Should().BeEmpty(
            "a route in docs/API.md that the host does not answer is a promise a consumer will " +
            "discover as a 404 in their own integration");
    }

    [Fact]
    public async Task Every_served_route_is_documented()
    {
        var documented = DocumentedRoutes();
        var served = await ServedRoutesAsync();

        string.Join(", ", served.Except(documented).Order(StringComparer.Ordinal)).Should().BeEmpty(
            "this surface is small and entirely public, so an endpoint the page does not mention " +
            "is one no consumer will find");
    }

    /// <summary>
    /// The routes listed in the route block at the top of the page, normalised so a documented
    /// parameter name and the template's need not agree letter for letter — the page names things
    /// for a reader, the template for a router.
    /// </summary>
    private static HashSet<string> DocumentedRoutes()
    {
        var api = RepoFile.Read("docs/API.md");

        // Only that block counts. The page also shows worked examples with concrete ids and query
        // strings, and reading those as the documented surface would make the gate assert that the
        // host serves one particular customer's order.
        var block = RouteBlock().Match(api);
        block.Success.Should().BeTrue("docs/API.md must carry a fenced yaml route block");

        return RouteLine().Matches(block.Groups["routes"].Value)
            .Select(m => Route(m.Groups["method"].Value, m.Groups["path"].Value))
            .ToHashSet(StringComparer.Ordinal);
    }

    private async Task<HashSet<string>> ServedRoutesAsync()
    {
        var document = JsonDocument.Parse(await _client.GetStringAsync("/openapi/v1.json"));

        var routes = document.RootElement.GetProperty("paths").EnumerateObject()
            .SelectMany(path => path.Value.EnumerateObject()
                .Select(method => Route(method.Name, path.Name)))
            .ToHashSet(StringComparer.Ordinal);

        // The document describes every endpoint except the route that serves the document itself,
        // which the page still has to mention for a consumer to know it is there.
        routes.Add(Route("GET", "/openapi/v1.json"));
        return routes;
    }

    /// <summary>
    /// Method and path together. A path alone would let a documented verb the host does not answer
    /// pass on the strength of a different verb on the same path — which is a 405 for the consumer,
    /// discovered exactly as late as the 404 this gate exists to prevent.
    /// </summary>
    private static string Route(string method, string path) =>
        $"{method.ToUpperInvariant()} {Normalize(path)}";

    /// <summary>Replaces every route parameter with a single placeholder.</summary>
    private static string Normalize(string path) => RouteParameter().Replace(path.Trim(), "{}");

    [GeneratedRegex("```yaml[\r\n]+(?<routes>.*?)```", RegexOptions.Singleline)]
    private static partial Regex RouteBlock();

    [GeneratedRegex(@"^(?<method>GET|POST|PUT|PATCH|DELETE)\s+(?<path>/\S*)", RegexOptions.Multiline)]
    private static partial Regex RouteLine();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex RouteParameter();
}
