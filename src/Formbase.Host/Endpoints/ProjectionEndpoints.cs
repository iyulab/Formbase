using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Host.Contracts;

namespace Formbase.Host.Endpoints;

/// <summary>
/// Running a projection and asking what state it is in. The two are one resource seen twice: the
/// POST rebuilds it, the GET reports what the last rebuild left behind.
/// </summary>
internal static class ProjectionEndpoints
{
    public static IEndpointRouteBuilder MapProjectionEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/formtypes/{type}/projection", RunAsync)
            .WithName("RunProjection")
            .WithSummary("Rebuilds a form type's projected table from raw")
            .WithDescription(
                "Safe to repeat and safe to retry: the table is rebuilt from the raw stream rather " +
                "than updated in place, so two runs over an unchanged stream leave the same table. " +
                "A run is bounded to the raw head it saw when it started, so documents that arrive " +
                "mid-run are left for the next one rather than landing under a watermark that does " +
                "not cover them. A form type with no declaration answers projected: false — " +
                "documents are accepted without one, so having none is not a failure.")
            .Produces<ProjectionRunResponse>();

        routes.MapGet("/formtypes/{type}/projection", GetStatusAsync)
            .WithName("GetProjectionStatus")
            .WithSummary("Reports whether a form type's projection exists and can be trusted")
            .WithDescription(
                "Distinguishes 'not projected yet' from 'no data', which a query alone cannot. " +
                "Branch on all four states — notProjected, projected, stale, unverified.")
            .Produces<ProjectionStatusResponse>();

        return routes;
    }

    private static async Task<IResult> RunAsync(
        string type,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        var result = await engine.ProjectAsync(FormTypeRef.Create(type), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ProjectionRunResponse(
            result.Projected,
            result.Inserted,
            result.ProjectedWatermark.Value,
            [.. result.Skipped.Select(s => new SkippedDocumentResponse(s.DocumentId.Value, s.Reason))],
            result.AbsentFieldCounts,
            result.UnresolvedReferences));
    }

    private static async Task<IResult> GetStatusAsync(
        string type,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        var status = await engine.GetProjectionStatusAsync(FormTypeRef.Create(type), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ProjectionStatusResponse(
            status.State.ToWire(),
            status.ProjectedWatermark.Value,
            status.RawHead.Value));
    }
}
