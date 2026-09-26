using Microsoft.AspNetCore.Http.HttpResults;

namespace Formbase.Host.Namespaces;

/// <summary>
/// Refuses a request addressed to a namespace this host does not serve, before it can reach an
/// endpoint that would answer from the one it does. Placed ahead of routing so the check cannot be
/// forgotten by an endpoint added later — the failure mode of a per-endpoint check is that the
/// newest endpoint is the one missing it.
/// <para>
/// On a durable host it also holds every request until the store is known to hold this namespace
/// (<see cref="NamespaceBinding"/>): the name a caller addressed is only the right one if the data
/// behind it is. Paths that read no store are let through unchecked: the probes (one that waited on
/// the check it reports would report nothing), and the host's description of itself (<c>/settings</c>,
/// <c>/openapi</c>) — which is what an operator reads to find out why the check is failing. Every other
/// path is checked, so an endpoint added later is held by default.
/// </para>
/// </summary>
internal sealed class NamespaceSelectorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly NamespaceSelector _selector;
    private readonly NamespaceBinding? _binding;

    public NamespaceSelectorMiddleware(RequestDelegate next, NamespaceSelector selector, IServiceProvider services)
    {
        _next = next;
        _selector = selector;
        _binding = services.GetService<NamespaceBinding>();
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requested = context.Request.Headers[NamespaceSelector.Header].ToString();

        if (!_selector.Accepts(requested))
        {
            // Naming the wrong namespace is a mistake about where the data lives, and answering from
            // this host's own would silently give the caller someone else's rows.
            await TypedResults.Problem(
                detail: $"This host serves the namespace '{_selector.Name}'. " +
                        $"Omit the {NamespaceSelector.Header} header to address it.",
                statusCode: StatusCodes.Status404NotFound,
                title: "No such namespace on this host",
                type: "/problems/unknown-namespace").ExecuteAsync(context).ConfigureAwait(false);
            return;
        }

        if (_binding is not null && !ReadsNoStore(context.Request.Path))
        {
            string? unverified = null;
            try
            {
                await _binding.EnsureAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (NamespaceBindingConflictException conflict)
            {
                unverified = conflict.Message;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Not the exception's own text: it may carry connection details.
                unverified = $"The store could not be reached to confirm it holds the namespace '{_selector.Name}'. Retry later.";
            }

            if (unverified is not null)
            {
                await TypedResults.Problem(
                    detail: unverified,
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "The host could not confirm its store holds this namespace",
                    type: "/problems/namespace-unverified").ExecuteAsync(context).ConfigureAwait(false);
                return;
            }
        }

        await _next(context).ConfigureAwait(false);
    }

    private static bool ReadsNoStore(PathString path) =>
        path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/settings", StringComparison.OrdinalIgnoreCase)
        || path.StartsWithSegments("/openapi", StringComparison.OrdinalIgnoreCase);
}
