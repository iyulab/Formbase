using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace Formbase.Host.Tests;

/// <summary>
/// Who may call. A host without keys answers everyone, as it always has; a host with keys answers only
/// a caller presenting one — on every route but the probes, so a route added later is held by default.
/// </summary>
public sealed class AccessKeyTests : IClassFixture<WebApplicationFactory<Program>>
{
    private static readonly string Key = new('a', 32);
    private static readonly string Rotated = new('b', 40);

    private readonly WebApplicationFactory<Program> _factory;

    public AccessKeyTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task A_host_without_keys_answers_a_caller_presenting_none()
    {
        using var client = _factory.CreateClient();

        var response = await client.GetAsync("/settings", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "no key configured is the default, and it changes nothing");
    }

    [Theory]
    [InlineData("/settings")]
    [InlineData("/openapi/v1.json")]
    [InlineData("/formtypes/orders/declaration")]
    public async Task A_keyed_host_refuses_a_caller_presenting_none(string path)
    {
        using var keyed = Keyed(Key);
        using var client = keyed.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "the host's description names where its data is kept, so it is held like the data");
        response.Headers.WwwAuthenticate.ToString().Should().Be("Bearer");
        var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken)).RootElement;
        problem.GetProperty("type").GetString().Should().Be("/problems/access-key-required");
    }

    [Fact]
    public async Task A_keyed_host_refuses_a_wrong_key_as_it_refuses_none()
    {
        using var keyed = Keyed(Key);
        using var client = keyed.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new string('x', 32));

        var response = await client.GetAsync("/settings", TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_keyed_host_answers_each_configured_key_so_one_can_be_rotated()
    {
        using var keyed = Keyed(Key, Rotated);

        foreach (var key in new[] { Key, Rotated })
        {
            using var client = keyed.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);

            var response = await client.GetAsync("/settings", TestContext.Current.CancellationToken);

            response.StatusCode.Should().Be(HttpStatusCode.OK);
        }
    }

    [Theory]
    [InlineData("/health")]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    public async Task The_probes_answer_without_a_key(string path)
    {
        using var keyed = Keyed(Key);
        using var client = keyed.CreateClient();

        var response = await client.GetAsync(path, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "an orchestrator asks before it knows any secret, and a probe carries no data");
    }

    [Fact]
    public void A_key_shorter_than_the_minimum_stops_the_host()
    {
        using var keyed = Keyed("short-key");

        var start = () => keyed.CreateClient();

        start.Should().Throw<Exception>().Which.ToString().Should().Contain("Formbase:Access:Keys");
    }

    private WebApplicationFactory<Program> Keyed(params string[] keys) =>
        _factory.WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, config) =>
                config.AddInMemoryCollection(keys
                    .Select((key, i) => new KeyValuePair<string, string?>($"Formbase:Access:Keys:{i}", key)))));
}
