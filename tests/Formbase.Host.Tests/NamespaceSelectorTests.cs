using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Formbase.Host.Tests;

/// <summary>
/// Which data a request is addressed to. A host serves one namespace, so the selection is
/// degenerate — which is exactly why it needs holding: a selector that accepted every value would
/// read as working right up to the first deployment that serves more than one, and every stored
/// request written without the header would have to be rewritten then instead of now.
/// </summary>
public sealed class NamespaceSelectorTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public NamespaceSelectorTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_request_naming_no_namespace_addresses_the_host_own()
    {
        var response = await SendAsync(_factory.CreateClient(), requested: null);

        response.StatusCode.Should().Be(HttpStatusCode.Created,
            "a single-namespace deployment must be usable without every caller knowing its name");
    }

    [Fact]
    public async Task A_request_naming_this_host_namespace_is_served()
    {
        var response = await SendAsync(_factory.CreateClient(), requested: "default");

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task A_request_naming_another_namespace_is_refused()
    {
        var response = await SendAsync(_factory.CreateClient(), requested: "somewhere-else");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound,
            "answering from this host's own data would hand the caller someone else's rows under " +
            "the name they asked for");

        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        problem.GetProperty("type").GetString().Should().Be("/problems/unknown-namespace");
        problem.GetProperty("detail").GetString().Should().Contain("default");
    }

    /// <summary>
    /// The name is configuration, not a constant. Without this, the selector would compare every
    /// request against a hard-coded value and still pass every test above.
    /// </summary>
    [Fact]
    public async Task The_namespace_a_host_answers_to_comes_from_its_configuration()
    {
        using var configured = _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["Formbase:Namespace"] = "orders-eu"
                })));

        using var client = configured.CreateClient();

        (await SendAsync(client, "orders-eu")).StatusCode.Should().Be(HttpStatusCode.Created);
        (await SendAsync(client, "default")).StatusCode.Should().Be(HttpStatusCode.NotFound,
            "a configured host no longer answers to the name it had by default");
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, string? requested)
    {
        var type = $"ns{Guid.NewGuid():N}"[..16];
        var request = new HttpRequestMessage(HttpMethod.Post, $"/formtypes/{type}/documents")
        {
            Content = new StringContent("""{"total":1}""", Encoding.UTF8, "application/json")
        };

        if (requested is not null)
        {
            request.Headers.Add("Formbase-Namespace", requested);
        }

        return client.SendAsync(request);
    }
}
