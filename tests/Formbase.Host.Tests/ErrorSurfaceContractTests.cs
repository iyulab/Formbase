using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Host.Tests;

/// <summary>
/// A caller's mistake answers 4xx, never 5xx. A 500 tells them to retry and wait for someone else
/// to fix it, when the thing that has to change is their own request — so the status is not a
/// cosmetic detail, it is the instruction they act on.
/// <para>
/// Every endpoint takes a form type from the path and validates it the same way, which means one
/// missing guard is invisible next to four working ones. These probe every route rather than a
/// representative sample: the endpoint most likely to be missing the guard is the one added last,
/// and a sample chosen today cannot include it.
/// </para>
/// </summary>
public sealed class ErrorSurfaceContractTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;

    public ErrorSurfaceContractTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
        _client = factory.CreateClient();
    }

    /// <summary>
    /// A form type is a validated primitive, so a blank one is refused where it is constructed —
    /// inside the engine, not at the edge. Every route that builds one therefore has to translate
    /// that refusal, and translating it once in the handler is what keeps a route added later from
    /// answering differently.
    /// </summary>
    private static readonly (string Method, string Template)[] Probed =
    [
        ("POST", "/formtypes/{type}/documents"),
        ("GET", "/formtypes/{type}/documents"),
        ("GET", "/formtypes/{type}/rows"),
        ("GET", "/formtypes/{type}/declaration"),
        ("PUT", "/formtypes/{type}/declaration"),
        ("DELETE", "/formtypes/{type}/declaration"),
        ("POST", "/formtypes/{type}/projection"),
        ("GET", "/formtypes/{type}/projection"),
        ("GET", "/formtypes/{type}/projection/skips"),
        ("GET", "/formtypes/{type}/records"),
        // A retirement also names the record; the form type is the only thing wrong with this request.
        ("DELETE", "/formtypes/{type}/records?recordKey=k"),
    ];

    public static TheoryData<string, string> RoutesTakingAFormType()
    {
        var data = new TheoryData<string, string>();
        foreach (var (method, template) in Probed)
        {
            data.Add(method, template);
        }

        return data;
    }

    /// <summary>
    /// The list above is only as complete as the person who last edited it, and a route added later is the one
    /// most likely to be missing the guard. So the host says which routes take a form type, and the list has
    /// to name every one of them.
    /// </summary>
    [Fact]
    public void Every_route_that_takes_a_form_type_is_probed()
    {
        var routed = _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText?.Contains("{type}", StringComparison.Ordinal) == true)
            .SelectMany(e => (e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? []).Select(m => $"{m} {e.RoutePattern.RawText}"))
            .ToHashSet(StringComparer.Ordinal);
        var probed = Probed.Select(p => $"{p.Method} {p.Template.Split('?')[0]}").ToHashSet(StringComparer.Ordinal);

        routed.Should().NotBeEmpty("the host maps routes that take a form type");
        routed.Except(probed).Should().BeEmpty("every route that takes a form type is probed for a blank one");
        probed.Except(routed).Should().BeEmpty("a probe names a route the host maps");
    }

    [Theory]
    [MemberData(nameof(RoutesTakingAFormType))]
    public async Task A_blank_form_type_is_the_callers_mistake_not_ours(string method, string template)
    {
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            template.Replace("{type}", "%20", StringComparison.Ordinal))
        {
            Content = new StringContent("""{"total":1}""", Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);
        var payload = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        ((int)response.StatusCode).Should().Be(400, payload);

        var problem = JsonElement.Parse(payload);
        problem.GetProperty("type").GetString().Should().Be("/problems/invalid-form-type",
            "the caller has to be told which part of their request to change");
    }

    /// <summary>
    /// The floor under the rest: whatever a route answers, it answers with something readable. A
    /// 5xx with an empty body leaves the caller with nothing to log and nothing to branch on.
    /// </summary>
    [Theory]
    [MemberData(nameof(RoutesTakingAFormType))]
    public async Task No_route_answers_with_an_empty_body(string method, string template)
    {
        using var request = new HttpRequestMessage(
            new HttpMethod(method),
            template.Replace("{type}", "%20", StringComparison.Ordinal))
        {
            Content = new StringContent("""{"total":1}""", Encoding.UTF8, "application/json")
        };

        var response = await _client.SendAsync(request, TestContext.Current.CancellationToken);

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).Should().NotBeEmpty();
    }
}
