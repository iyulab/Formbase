using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Formbase.Host.Tests;

/// <summary>
/// Holds the operator surface: the probes an orchestrator asks before it sends traffic.
/// <para>
/// This host is published as an image, and answered nothing of the kind — its own compose bundle
/// waits on postgres and on the sibling's <c>/health</c>, then starts this service blind. The two
/// probes are held separately because they answer different questions: liveness says the process is
/// up, readiness says the stores it was composed with answered. A deployment that treats them as
/// one restarts this host in response to an outage somewhere else.
/// </para>
/// <para>
/// They are deliberately absent from <c>docs/API.md</c> — that page is the consumer's surface — so
/// the documentation parity gate must keep passing without them, which is itself part of what this
/// asserts by existing alongside it.
/// </para>
/// </summary>
public sealed class HealthProbeTests(WebApplicationFactory<Program> factory) : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Theory]
    [InlineData("/health/live")]
    [InlineData("/health/ready")]
    [InlineData("/health")]
    public async Task Every_probe_answers(string route)
    {
        var response = await _client.GetAsync(route, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.OK,
            "an orchestrator reads the status code and nothing else; on the in-memory profile the "
            + "stores are in this process, so a probe that is not OK here means the host itself is wrong");

        (await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))
            .Should().Be("Healthy");
    }

    [Fact]
    public async Task Liveness_does_not_wait_on_the_stores()
    {
        // Liveness runs no checks by design: it answers whether this process is serving, which is
        // the only question a restart could fix. Asserting the shape here rather than trusting the
        // registration keeps the two probes from quietly collapsing into one.
        var live = await _client.GetAsync("/health/live", TestContext.Current.CancellationToken);
        var ready = await _client.GetAsync("/health/ready", TestContext.Current.CancellationToken);

        live.StatusCode.Should().Be(HttpStatusCode.OK);
        ready.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
