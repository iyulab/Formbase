using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Host.Contracts;

namespace Formbase.Host.Endpoints;

/// <summary>
/// Reading the declaration an instance currently holds.
/// <para>
/// Reading is separable from writing, and only reading is here. Who evolves a declaration is a
/// product question with consequences for versioning, re-projection and deletion; reading back the
/// one in force is correct whatever that answer turns out to be, and it is what a caller needs in
/// order to tell a stale deployment from a current one.
/// </para>
/// </summary>
internal static class DeclarationEndpoints
{
    public static IEndpointRouteBuilder MapDeclarationEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/formtypes/{type}/declaration", GetAsync)
            .WithName("GetDeclaration")
            .WithSummary("Reads the declaration a form type currently has")
            .WithDescription(
                "Answers with the declaration in force — the shape the next projection run will " +
                "build. A form type with none is not an error state: documents are accepted without " +
                "a declaration, and the raw store keeps them until one arrives.")
            .Produces<DeclarationResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<IResult> GetAsync(
        string type,
        IFieldHintSource hints,
        CancellationToken cancellationToken)
    {
        var formType = FormTypeRef.Create(type);
        var declaration = await hints.GetHintsAsync(formType, cancellationToken).ConfigureAwait(false);

        if (declaration is null)
        {
            return Results.Problem(
                detail: $"Form type '{formType}' has no declaration. Documents are accepted without " +
                        "one and are kept in the raw store, so this is a state to read, not a failure " +
                        "to recover from.",
                statusCode: StatusCodes.Status404NotFound,
                title: "The form type has no declaration",
                type: "/problems/no-declaration");
        }

        return Results.Ok(new DeclarationResponse(
            declaration.Type.Value,
            declaration.TableName,
            declaration.DeclarationVersion,
            [.. declaration.Fields.Select(f => new DeclaredFieldResponse(
                f.Name,
                f.Type.ToWire(),
                f.Nullable,
                f.SourceKey,
                f.Binding.ToWire(),
                f.Target is null ? null : new DeclaredTargetResponse(f.Target.Entity.Value, f.Target.KeyField)))],
            [.. (declaration.Relations ?? []).Select(r => new DeclaredRelationResponse(
                r.Name,
                r.Kind.ToWire(),
                r.Target.Value,
                r.KeyField))]));
    }
}
