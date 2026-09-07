using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Host.Contracts;
using Formbase.Host.Projection;

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

        routes.MapGet("/formtypes/{type}/projection/skips", GetSkipsAsync)
            .WithName("GetProjectionSkips")
            .WithSummary("Lists the documents the last projection could not map, and why")
            .WithDescription(
                "The status endpoint reports that a projection caught up with raw; this reports what " +
                "it left behind getting there. A run that inserted 2 of 150 documents is 'projected' " +
                "and current on that endpoint — the 148 reasons are here. Recorded with the " +
                "projection itself, so it survives a restart and answers for runs another instance " +
                "performed; a new run replaces it, because skips describe one run. Empty both when " +
                "nothing was skipped and when the form type was never projected — the status " +
                "endpoint is what separates those.")
            .Produces<ProjectionSkipsResponse>();

        return routes;
    }

    private static async Task<IResult> RunAsync(
        string type,
        FormbaseEngine engine,
        LastProjectionRunTracker runTracker,
        CancellationToken cancellationToken)
    {
        var formType = FormTypeRef.Create(type);
        var result = await engine.ProjectAsync(formType, cancellationToken)
            .ConfigureAwait(false);

        runTracker.Record(formType, result);

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
        LastProjectionRunTracker runTracker,
        CancellationToken cancellationToken)
    {
        var formType = FormTypeRef.Create(type);
        var status = await engine.GetProjectionStatusAsync(formType, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ProjectionStatusResponse(
            status.State.ToWire(),
            status.ProjectedWatermark.Value,
            status.RawHead.Value,
            LastRunResponse.FromTracked(runTracker.TryGet(formType))));
    }

    private static async Task<IResult> GetSkipsAsync(
        string type,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        var skips = await engine.GetProjectionSkipsAsync(FormTypeRef.Create(type), cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new ProjectionSkipsResponse(
            [.. skips.Select(s => new SkippedDocumentResponse(s.DocumentId.Value, s.Reason))],
            skips.Count));
    }
}
