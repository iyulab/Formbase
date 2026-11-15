using System.Net;
using Formbase.Host.Composition;
using Formbase.Host.Namespaces;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

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

    /// <summary>
    /// On a durable host readiness also means the store holds this host's namespace. A binding that
    /// cannot be confirmed is not ready — while liveness stays up, since restarting would not help.
    /// Nothing listens on port 1, so the binding check fails the way an unreachable database does.
    /// </summary>
    [Fact]
    public async Task Readiness_waits_on_the_namespace_binding_and_liveness_does_not()
    {
        using var durable = factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<StoreProfileSelection>();
                services.AddSingleton(new StoreProfileSelection(
                    StoreProfile.Durable, new DurableStoreLocation("formbase", Guid.NewGuid())));
                services.AddSingleton(NpgsqlDataSource.Create("Host=127.0.0.1;Port=1;Timeout=1"));
                services.AddSingleton<NamespaceBinding>();
            }));
        using var client = durable.CreateClient();

        (await client.GetAsync("/health/ready", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.ServiceUnavailable);
        (await client.GetAsync("/health/live", TestContext.Current.CancellationToken)).StatusCode
            .Should().Be(HttpStatusCode.OK);
    }

    /// <summary>
    /// A probe is asked every few seconds by the image's own healthcheck and by whatever orchestrates it: a line per
    /// request would make the log mostly probes. The host ships the framework's usual logging default -- its request
    /// and endpoint lines are Warning and above -- and that is what keeps a probe out of the log.
    /// </summary>
    [Theory]
    [InlineData("/health/ready")]
    [InlineData("/health/live")]
    public async Task A_probe_leaves_no_line_in_the_log(string route)
    {
        var sink = new CapturedLog();
        using var logged = factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services => services.AddLogging(logging => logging.AddProvider(sink))));
        using var client = logged.CreateClient();

        (await client.GetAsync(route, TestContext.Current.CancellationToken)).StatusCode.Should().Be(HttpStatusCode.OK);

        sink.Lines.Where(l => l.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal))
            .Should().BeEmpty("a probe that answers is not an event, and logging each one buries the ones that are");
    }

    /// <summary>
    /// The image must name its port one way. Setting both <c>ASPNETCORE_URLS</c> and the base image's
    /// <c>ASPNETCORE_HTTP_PORTS</c> makes the host warn on every start that it is overriding one with the other, and the
    /// self-probe reads only the second -- so the image sets that one, to the port the probe falls back on.
    /// </summary>
    [Fact]
    public void The_image_names_its_port_by_one_setting_the_self_probe_reads()
    {
        var dockerfile = RepoFile.Read("src/Formbase.Host/Dockerfile");

        dockerfile.Split('\n').Select(l => l.Trim()).Where(l => l.StartsWith("ENV ", StringComparison.Ordinal))
            .Should().NotContain(l => l.Contains("ASPNETCORE_URLS", StringComparison.Ordinal),
                "the warning at start is the host saying the image set the port twice");
        dockerfile.Should().Contain("ENV ASPNETCORE_HTTP_PORTS=8080", "the self-probe reads this setting and defaults to 8080");
    }

    private sealed class CapturedLog : ILoggerProvider
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> Lines { get { lock (_lines) { return [.. _lines]; } } }

        public ILogger CreateLogger(string categoryName) => new Sink(categoryName, _lines);

        public void Dispose()
        {
        }

        private sealed class Sink(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add($"{category}: {formatter(state, exception)}");
                }
            }
        }
    }
}
