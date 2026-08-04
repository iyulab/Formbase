using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Formbase.Host.Namespaces;

/// <summary>
/// Declares the namespace selector on every operation in the generated document.
/// <para>
/// The selector is enforced in middleware, ahead of routing, so no endpoint signature mentions it
/// and nothing would otherwise put it in the document. A consumer generating a client would then
/// get one that cannot address a namespace at all — which is the same failure as not having the
/// selector, arriving by a different route. The argument for shipping the header now, rather than
/// when a host serves more than one namespace, applies with equal force to the generated client.
/// </para>
/// </summary>
internal sealed class NamespaceHeaderDocumentTransformer : IOpenApiDocumentTransformer
{
    public Task TransformAsync(
        OpenApiDocument document,
        OpenApiDocumentTransformerContext context,
        CancellationToken cancellationToken)
    {
        foreach (var operation in document.Paths.Values.SelectMany(path => path.Operations!.Values))
        {
            operation.Parameters ??= [];
            operation.Parameters.Add(new OpenApiParameter
            {
                Name = NamespaceSelector.Header,
                In = ParameterLocation.Header,
                Required = false,
                Description =
                    "Which namespace the request is addressed to. Omit it to address the host's " +
                    "own; naming one this host does not serve is refused rather than answered " +
                    "from the one it does.",
                Schema = new OpenApiSchema { Type = JsonSchemaType.String }
            });
        }

        return Task.CompletedTask;
    }
}
