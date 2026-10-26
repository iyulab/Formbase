using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Host.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Formbase.Host.Endpoints;

/// <summary>
/// The system's question: reading a form type's projected records — and retiring one, which the
/// projection stops showing while its documents stay in the raw stream.
/// <para>
/// Unlike a document read, this depends on a projection existing. A form type that has never been
/// projected answers with a refusal rather than an empty page — "nothing matched" and "nothing has
/// been built yet" are different facts, and a caller that cannot tell them apart writes the wrong
/// remedy.
/// </para>
/// </summary>
internal static class RecordEndpoints
{
    public static IEndpointRouteBuilder MapRecordEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapGet("/formtypes/{type}/records", QueryAsync)
            .WithName("QueryRecords")
            .WithSummary("Reads a form type's projected records")
            .WithDescription(
                "Filters are equality only, one per `filter=column:value` parameter; the first colon " +
                "separates the two, so a value may contain colons. Values are compared as the " +
                "declared column's type, so `filter=total:42` matches a number. Ordering takes a " +
                "comma-separated column list where a leading `-` reverses that column. Paging is " +
                "deterministic whether or not the caller orders: the projection's watermark is " +
                "appended as a final tie-breaker. `referenceSources=true` adds, for each reference " +
                "field, the target record its value was read from and what reading it found.")
            .Produces<RecordQueryResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status503ServiceUnavailable);

        routes.MapDelete("/formtypes/{type}/records", RetireAsync)
            .WithName("RetireRecord")
            .WithSummary("Retires a record")
            .WithDescription(
                "Appends a retirement of the record named by `recordKey`: the projection stops showing it, " +
                "and its documents stay in the raw stream, where the retirement appears as a document with " +
                "`retired: true` and no body. Appending under the key again brings the record back. Nothing " +
                "checks that the key was ever used. An Idempotency-Key header works as it does for intake. " +
                "A repeated section's form type has no records of its own to retire and is refused with 409.")
            .Produces<AcceptedDocumentResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status409Conflict)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        return routes;
    }

    private static async Task<IResult> RetireAsync(
        string type,
        [FromHeader(Name = DocumentEndpoints.IdempotencyKeyHeader)] string? idempotencyKey,
        string? recordKey,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        if (!DocumentEndpoints.TryReadIdempotencyKey(idempotencyKey, out var idempotencyId, out var problem))
        {
            return problem;
        }

        if (string.IsNullOrWhiteSpace(recordKey))
        {
            return Problem("recordKey names the record to retire and is required.");
        }

        var key = RecordKey.Create(recordKey);
        var id = await engine.RetireAsync(FormTypeRef.Create(type), key, idempotencyId, cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new AcceptedDocumentResponse(id.Value, type, key.Value));
    }

    private static async Task<IResult> QueryAsync(
        string type,
        HttpRequest request,
        FormbaseEngine engine,
        int? limit,
        int? offset,
        string? orderBy,
        bool? referenceSources,
        CancellationToken cancellationToken)
    {
        if (RecordReads.Spec([.. request.Query["filter"]], orderBy, limit, offset, referenceSources, out var problem) is not { } spec)
        {
            return Problem(problem!);
        }

        return Results.Ok(await RecordReads.ReadAsync(engine, type, spec, cancellationToken).ConfigureAwait(false));
    }

    private static IResult Problem(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "The query could not be read",
            type: "/problems/invalid-query");
}
