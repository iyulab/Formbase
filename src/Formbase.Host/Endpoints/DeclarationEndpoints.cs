using Formbase.Core;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Formbase.Host.Contracts;
using Formbase.Host.Declarations;

namespace Formbase.Host.Endpoints;

/// <summary>
/// Reading and writing the declaration an instance holds.
/// <para>
/// A declaration is the shape the next projection builds, and the instance owns it — so replacing
/// one is a write like any other, with the two consequences that come with shared ownership:
/// whoever replaces it has to say which version they were replacing, and a shape that changed
/// leaves any existing projection stale.
/// </para>
/// <para>
/// Deleting a declaration is not here yet.
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

        routes.MapPut("/formtypes/{type}/declaration", DeclareAsync)
            .WithName("Declare")
            .WithSummary("Puts a declaration in force for a form type")
            .WithDescription(
                "Send expectedDeclarationVersion with the version you believe is in force. Omit it " +
                "only for a form type that has none: replacing a declaration without naming the one " +
                "being replaced is how one consumer silently discards another's. Nothing is rebuilt " +
                "here — the reply reports what the write did to the projection, and a rebuild drops " +
                "and refills the whole table, which is not a cost to spend on a caller's behalf " +
                "without being asked.")
            .Produces<DeclarationWriteResponse>()
            .Produces<DeclarationWriteResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict);

        return routes;
    }

    private static async Task<IResult> GetAsync(
        string type,
        IFieldHintSource hints,
        CancellationToken cancellationToken)
    {
        var formType = FormTypeRef.Create(type);
        var declaration = await hints.GetHintsAsync(formType, cancellationToken).ConfigureAwait(false);

        return declaration is null
            ? Results.Problem(
                detail: $"Form type '{formType}' has no declaration. Documents are accepted without " +
                        "one and are kept in the raw store, so this is a state to read, not a failure " +
                        "to recover from.",
                statusCode: StatusCodes.Status404NotFound,
                title: "The form type has no declaration",
                type: "/problems/no-declaration")
            : Results.Ok(Describe(declaration));
    }

    private static async Task<IResult> DeclareAsync(
        string type,
        DeclarationRequest request,
        IFieldHintSource hints,
        IDeclarationWriter writer,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        var formType = FormTypeRef.Create(type);

        if (Rejected(request) is { } invalid)
        {
            return invalid;
        }

        var existing = await hints.GetHintsAsync(formType, cancellationToken).ConfigureAwait(false);
        if (Conflict(existing, request.ExpectedDeclarationVersion) is { } conflict)
        {
            return conflict;
        }

        await writer.DeclareAsync(ToEngine(formType, request), cancellationToken).ConfigureAwait(false);

        // Read back rather than echo: the caller needs what is in force, and only the store knows
        // whether it stored what it was handed.
        var stored = await hints.GetHintsAsync(formType, cancellationToken).ConfigureAwait(false);
        var status = await engine.GetProjectionStatusAsync(formType, cancellationToken).ConfigureAwait(false);
        var body = new DeclarationWriteResponse(Describe(stored!), Describe(status));

        return existing is null
            ? Results.Created($"/formtypes/{formType}/declaration", body)
            : Results.Ok(body);
    }

    /// <summary>
    /// What the store cannot be asked to hold. Each of these would otherwise land as a projected
    /// table nobody meant to declare.
    /// </summary>
    private static IResult? Rejected(DeclarationRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.TableName))
        {
            return Invalid("The declaration must name the table its projection builds.");
        }

        if (request.Fields is not { Count: > 0 })
        {
            return Invalid(
                "The declaration must carry at least one field. A declaration with none would " +
                "propose a table with nothing in it, which is not the same as having no declaration.");
        }

        var blank = request.Fields.FirstOrDefault(f => string.IsNullOrWhiteSpace(f.Name));
        if (blank is not null)
        {
            return Invalid("A declared field must have a name.");
        }

        var duplicate = request.Fields
            .GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);

        return duplicate is null
            ? null
            : Invalid(
                $"The field '{duplicate.Key}' is declared more than once. One of them would land in " +
                "the projected column and the other would vanish without being reported.");
    }

    /// <summary>
    /// The version check. A first declaration has nothing to expect and a replacement has to name
    /// what it replaces — the two mistakes this catches are opposite beliefs about whether a
    /// declaration is already there.
    /// </summary>
    private static IResult? Conflict(FormTypeHints? existing, int? expected) => (existing, expected) switch
    {
        (null, null) => null,
        (null, not null) => Conflicted(
            $"No declaration is in force, but the request expected version {expected}. " +
            "Omit expectedDeclarationVersion to declare for the first time."),
        (not null, null) => Conflicted(
            $"A declaration is already in force at version {existing.DeclarationVersion}. " +
            "Send expectedDeclarationVersion to replace it deliberately."),
        (not null, not null) when existing.DeclarationVersion != expected => Conflicted(
            $"The declaration in force is version {existing.DeclarationVersion}, not {expected}. " +
            "Read it back and replace the one that is actually there."),
        _ => null,
    };

    private static FormTypeHints ToEngine(FormTypeRef formType, DeclarationRequest request) =>
        new(
            formType,
            request.TableName,
            [.. request.Fields.Select(f => new FieldHint(
                f.Name,
                f.Type.ToEngine(),
                f.Nullable,
                f.SourceKey,
                f.Binding.ToEngine(),
                f.Target is null
                    ? null
                    : new EntityRef(FormTypeRef.Create(f.Target.FormType), f.Target.KeyField)))],
            request.Relations is null
                ? null
                : [.. request.Relations.Select(r => new RelationHint(
                    r.Name, r.Kind.ToEngine(), FormTypeRef.Create(r.Target), r.KeyField))],
            request.DeclarationVersion);

    private static DeclarationResponse Describe(FormTypeHints declaration) =>
        new(
            declaration.Type.Value,
            declaration.TableName,
            declaration.DeclarationVersion,
            [.. declaration.Fields.Select(f => new DeclaredFieldResponse(
                f.Name,
                f.Type.ToWire(),
                f.Nullable,
                f.SourceKey,
                f.Binding.ToWire(),
                f.Target is null
                    ? null
                    : new DeclaredTargetResponse(f.Target.Entity.Value, f.Target.KeyField)))],
            [.. (declaration.Relations ?? []).Select(r => new DeclaredRelationResponse(
                r.Name,
                r.Kind.ToWire(),
                r.Target.Value,
                r.KeyField))]);

    private static ProjectionStatusResponse Describe(ProjectionStatus status) =>
        new(status.State.ToWire(), status.ProjectedWatermark.Value, status.RawHead.Value);

    private static IResult Invalid(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "The declaration could not be read",
            type: "/problems/invalid-declaration");

    private static IResult Conflicted(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status409Conflict,
            title: "The declaration in force is not the one the request expected",
            type: "/problems/declaration-version-conflict");
}
