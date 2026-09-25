using System.Text.Json;
using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Host.Contracts;
using Microsoft.AspNetCore.Mvc;

namespace Formbase.Host.Endpoints;

/// <summary>
/// Intake and raw reads — the operations that hold whatever a form type's declaration and projection
/// are doing. Accepting a document never requires a declaration, and reading documents back — one by
/// id, or a form type's stream page by page — is always available, so these are the endpoints a
/// caller can rely on before anything else exists.
/// </summary>
internal static class DocumentEndpoints
{
    /// <summary>
    /// The idempotency key travels in a header rather than in the body because the body is stored
    /// verbatim: a key written into it would become part of the document the caller sent.
    /// </summary>
    internal const string IdempotencyKeyHeader = "Idempotency-Key";

    /// <summary>The page a stream read returns when the caller names no size.</summary>
    internal const int DefaultPageSize = 100;

    /// <summary>The largest page a stream read returns — one request holds one page in memory.</summary>
    internal const int MaxPageSize = 1000;

    public static IEndpointRouteBuilder MapDocumentEndpoints(this IEndpointRouteBuilder routes)
    {
        routes.MapPost("/formtypes/{type}/documents", AcceptAsync)
            .WithName("AcceptDocument")
            .WithSummary("Accepts a document into the raw store")
            .WithDescription(
                "The document is stored verbatim; no declaration is required and none is consulted. " +
                "Send an Idempotency-Key header to make re-submission safe: the same key returns the " +
                "same document without appending a second copy. A key already used for a document " +
                "of another form type is refused with 422.")
            .Produces<AcceptedDocumentResponse>(StatusCodes.Status201Created)
            .ProducesProblem(StatusCodes.Status400BadRequest)
            .ProducesProblem(StatusCodes.Status422UnprocessableEntity);

        routes.MapGet("/formtypes/{type}/documents", ListAsync)
            .WithName("ListDocuments")
            .WithSummary("Reads a page of a form type's raw stream")
            .WithDescription(
                "Documents come back as they were accepted, oldest first, after the `after` watermark " +
                "(default 0, the start of the stream) — whether or not the form type has a declaration " +
                "or a projection, so fields nothing has declared are readable here. `limit` defaults to " +
                "100 and is at most 1000; `limit=0` reads only `rawHead`. The page never reaches past " +
                "`rawHead`, so a caller continues from the last watermark it received until that " +
                "watermark equals `rawHead`.")
            .Produces<DocumentPageResponse>()
            .ProducesProblem(StatusCodes.Status400BadRequest);

        routes.MapGet("/documents/{id:guid}", GetAsync)
            .WithName("GetDocument")
            .WithSummary("Reads a stored document")
            .WithDescription(
                "Reads from the raw store, which is the source of truth. This answers whether or not " +
                "the form type has ever been projected.")
            .Produces<StoredDocumentResponse>()
            .ProducesProblem(StatusCodes.Status404NotFound);

        return routes;
    }

    private static async Task<IResult> AcceptAsync(
        string type,
        [FromHeader(Name = IdempotencyKeyHeader)] string? idempotencyKey,
        HttpRequest request,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        // A blank form type is refused by FormTypeRef.Create below and translated by the problem
        // handler, so it is not guarded here — one answer, not two that can drift.
        DocumentId? idempotencyId = null;
        if (!string.IsNullOrEmpty(idempotencyKey))
        {
            if (!Guid.TryParse(idempotencyKey, out var key))
            {
                return Problem(
                    $"The {IdempotencyKeyHeader} header must be a UUID. It becomes the document's " +
                    "identity, so a value that cannot be one would silently stop deduplicating.");
            }

            idempotencyId = DocumentId.From(key);
        }

        DocumentBody body;
        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            body = DocumentBody.From(document.RootElement);
        }
        catch (JsonException ex)
        {
            return Problem($"The request body is not valid JSON: {ex.Message}");
        }

        var id = await engine.AcceptAsync(FormTypeRef.Create(type), body, idempotencyId, cancellationToken)
            .ConfigureAwait(false);

        // A repeat of an accepted key answers exactly as the first call did — that identity is the
        // whole point of the key, so the reply must not encode which attempt this was.
        return Results.Created($"/documents/{id.Value}", new AcceptedDocumentResponse(id.Value, type));
    }

    private static async Task<IResult> ListAsync(
        string type,
        long? after,
        int? limit,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        if (after is < 0)
        {
            return Problem("after is a watermark and cannot be negative; omit it to read from the start.");
        }

        // Refused rather than clamped: a caller who asked for more than a page holds and silently
        // received fewer would read the short page as the end of the stream.
        if (limit is < 0 or > MaxPageSize)
        {
            return Problem($"limit must be between 0 and {MaxPageSize}.");
        }

        var page = await engine.ReadDocumentsAsync(
                FormTypeRef.Create(type),
                new Watermark(after ?? 0),
                limit ?? DefaultPageSize,
                cancellationToken)
            .ConfigureAwait(false);

        return Results.Ok(new DocumentPageResponse(
            page.Documents.Select(ToResponse).ToList(),
            page.RawHead.Value));
    }

    private static async Task<IResult> GetAsync(
        Guid id,
        FormbaseEngine engine,
        CancellationToken cancellationToken)
    {
        var stored = await engine.GetDocumentAsync(DocumentId.From(id), cancellationToken).ConfigureAwait(false);

        return stored is null
            ? Results.Problem(
                detail: $"No document with id '{id}'.",
                statusCode: StatusCodes.Status404NotFound,
                title: "No such document",
                type: "/problems/no-such-document")
            : Results.Ok(ToResponse(stored));
    }

    private static StoredDocumentResponse ToResponse(StoredDocument stored) =>
        new(stored.Id.Value, stored.Type.Value, stored.Watermark.Value, stored.AppendedAt, stored.Body.Root);

    private static IResult Problem(string detail) =>
        Results.Problem(
            detail: detail,
            statusCode: StatusCodes.Status400BadRequest,
            title: "The request could not be read",
            type: "/problems/invalid-request");
}
