using Microsoft.AspNetCore.Http.HttpResults;

namespace Formbase.Host.Access;

/// <summary>
/// Refuses a request that does not present one of the host's access keys, when keys are configured
/// (<see cref="AccessKeys"/>). Placed ahead of everything that reads data, so an endpoint added later
/// is held by default — the failure mode of a per-endpoint check is that the newest endpoint is the one
/// missing it.
/// <para>
/// Only the probes pass without a key: an orchestrator asks them before it knows any secret, and they
/// carry no data. <c>/settings</c> and <c>/openapi</c> do not — they name where the data is kept.
/// </para>
/// </summary>
internal sealed partial class AccessKeyMiddleware
{
    private const string Scheme = "Bearer ";

    private readonly RequestDelegate _next;
    private readonly AccessKeys _keys;

    public AccessKeyMiddleware(RequestDelegate next, AccessKeys keys, ILogger<AccessKeyMiddleware> logger)
    {
        _next = next;
        _keys = keys;
        if (!keys.Required)
        {
            // Said once, as the pipeline is built: an open host is a deployment choice, and one nobody
            // made on purpose should be visible in the first lines of its log.
            LogOpen(logger);
        }
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_keys.Required || context.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var header = context.Request.Headers.Authorization.ToString();
        if (header.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase) && _keys.Accepts(header[Scheme.Length..].Trim()))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // A missing key and a wrong one answer alike: telling them apart tells a prober which it has.
        context.Response.Headers.WWWAuthenticate = "Bearer";
        await TypedResults.Problem(
            detail: "This host requires an access key. Send one of its keys as 'Authorization: Bearer <key>'.",
            statusCode: StatusCodes.Status401Unauthorized,
            title: "An access key is required",
            type: "/problems/access-key-required").ExecuteAsync(context).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "No access key is configured (Formbase:Access:Keys): this host answers every caller that can reach it. Keep it on a network only its callers reach, or configure a key.")]
    private static partial void LogOpen(ILogger logger);
}
