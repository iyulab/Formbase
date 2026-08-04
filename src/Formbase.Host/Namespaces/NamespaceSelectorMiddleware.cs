using Microsoft.AspNetCore.Http.HttpResults;

namespace Formbase.Host.Namespaces;

/// <summary>
/// Refuses a request addressed to a namespace this host does not serve, before it can reach an
/// endpoint that would answer from the one it does. Placed ahead of routing so the check cannot be
/// forgotten by an endpoint added later — the failure mode of a per-endpoint check is that the
/// newest endpoint is the one missing it.
/// </summary>
internal sealed class NamespaceSelectorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly NamespaceSelector _selector;

    public NamespaceSelectorMiddleware(RequestDelegate next, NamespaceSelector selector)
    {
        _next = next;
        _selector = selector;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requested = context.Request.Headers[NamespaceSelector.Header].ToString();

        if (_selector.Accepts(requested))
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // Naming the wrong namespace is a mistake about where the data lives, and answering from
        // this host's own would silently give the caller someone else's rows.
        var problem = TypedResults.Problem(
            detail: $"This host serves the namespace '{_selector.Name}'. " +
                    $"Omit the {NamespaceSelector.Header} header to address it.",
            statusCode: StatusCodes.Status404NotFound,
            title: "No such namespace on this host",
            type: "/problems/unknown-namespace");

        await problem.ExecuteAsync(context).ConfigureAwait(false);
    }
}
