using Formbase.Core;
using Formbase.Core.Primitives;
using Formbase.Host.Contracts;

namespace Formbase.Host.Endpoints;

/// <summary>
/// Reading a form type's raw stream and single documents by the document endpoints' rules, in one place: the REST reads
/// and the MCP read tools both ask here, so what a caller may page by and what a document looks like is the same on
/// either door.
/// </summary>
internal static class DocumentReads
{
    /// <summary>What a negative watermark is refused with.</summary>
    internal const string NegativeAfter = "after is a watermark and cannot be negative; omit it to read from the start.";

    /// <summary>One page of the stream after a watermark, oldest first, never reaching past the stream's head.</summary>
    /// <exception cref="ArgumentException">The form type's name cannot be one.</exception>
    internal static async Task<DocumentPageResponse> ListAsync(FormbaseEngine engine, string type, long after, int limit, CancellationToken cancellationToken)
    {
        var page = await engine.ReadDocumentsAsync(FormTypeRef.Create(type), new Watermark(after), limit, cancellationToken).ConfigureAwait(false);
        return new DocumentPageResponse([.. page.Documents.Select(ToResponse)], page.RawHead.Value);
    }

    /// <summary>
    /// One page of a repeated section's rows after a watermark of its parent's stream, or <see langword="null"/> when the form
    /// type is not a repeated section. The rows are the ones the section's projection cuts, named for citation.
    /// </summary>
    /// <exception cref="ArgumentException">The form type's name cannot be one.</exception>
    internal static async Task<SectionRowPageResponse?> ListSectionRowsAsync(FormbaseEngine engine, string type, long after, int limit, CancellationToken cancellationToken)
    {
        if (await engine.ReadSectionRowsAsync(FormTypeRef.Create(type), new Watermark(after), limit, cancellationToken).ConfigureAwait(false) is not { } page)
        {
            return null;
        }

        return new SectionRowPageResponse(
            [.. page.Documents.Select(d => new SectionDocumentResponse(
                d.Document.Id.Value,
                d.Document.Watermark.Value,
                d.Document.Key?.Value,
                d.Document.IsRetirement,
                [.. (d.Expansion?.Rows ?? []).Select(r => new SectionRowResponse(r.Position, r.Citation, r.Record, r.Body))],
                d.Expansion?.Refusal))],
            page.RawHead.Value);
    }

    /// <summary>One stored document, or <see langword="null"/> when there is none with this id.</summary>
    internal static async Task<StoredDocumentResponse?> GetAsync(FormbaseEngine engine, Guid id, CancellationToken cancellationToken) =>
        await engine.GetDocumentAsync(DocumentId.From(id), cancellationToken).ConfigureAwait(false) is { } stored ? ToResponse(stored) : null;

    internal static StoredDocumentResponse ToResponse(StoredDocument stored) =>
        new(
            stored.Id.Value,
            stored.Type.Value,
            stored.Watermark.Value,
            stored.AppendedAt,
            stored.Body?.Root,
            stored.Key?.Value,
            stored.IsRetirement);
}
