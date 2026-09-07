namespace Formbase.Host.Health;

/// <summary>
/// Lets the container ask this host whether it is ready, using nothing but itself.
/// <para>
/// The runtime image carries neither curl nor wget, so a <c>HEALTHCHECK</c> written the usual way
/// reports unhealthy for a reason that has nothing to do with the service. The Dockerfile said so
/// and left readiness to be observed from outside "until the host grows a probe of its own". This
/// is that probe: the same binary, invoked with a flag, asking the port it would otherwise serve.
/// </para>
/// <para>
/// It exits rather than returning a value, because a healthcheck reads an exit code and nothing
/// else. Any failure at all is unready -- a probe that tried to distinguish a refused connection
/// from a 503 would be answering a question the orchestrator did not ask.
/// </para>
/// </summary>
internal static class SelfProbe
{
    public const string Flag = "--health-check";

    public static async Task<int> RunAsync(string route)
    {
        var port = Environment.GetEnvironmentVariable("ASPNETCORE_HTTP_PORTS") ?? "8080";
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        try
        {
            var response = await client.GetAsync($"http://localhost:{port}{route}").ConfigureAwait(false);
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch
        {
            return 1;
        }
    }
}
