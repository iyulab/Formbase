using System.Text.Json;

namespace Formbase.Host.Contracts;

/// <summary>
/// What intake answers: the document's identity, which is also the idempotency key for any repeat
/// of the same submission.
/// </summary>
/// <param name="DocumentId">Identity of the stored document.</param>
/// <param name="FormType">The form type the document was accepted under.</param>
/// <param name="RecordKey">The record the document belongs to, when the request named one.</param>
public sealed record AcceptedDocumentResponse(Guid DocumentId, string FormType, string? RecordKey = null);

/// <summary>
/// A document as the raw store holds it. The body is returned as it was sent — the store keeps it
/// verbatim, and reinterpreting it on the way out would make the reply a different document from
/// the one that was accepted.
/// </summary>
/// <param name="DocumentId">Identity of the stored document.</param>
/// <param name="FormType">The form type the document was accepted under.</param>
/// <param name="Watermark">Position in the form type's append-only stream.</param>
/// <param name="AppendedAt">When the document entered the raw store.</param>
/// <param name="Body">The document content, verbatim — null for a retirement.</param>
/// <param name="RecordKey">The record the document belongs to, or null for a record of its own.</param>
/// <param name="Retired">
/// Whether this document retires <paramref name="RecordKey"/> rather than carrying content. Stated rather
/// than implied by a null body, because a document whose content is the JSON value <c>null</c> has a null
/// body too.
/// </param>
public sealed record StoredDocumentResponse(
    Guid DocumentId,
    string FormType,
    long Watermark,
    DateTimeOffset AppendedAt,
    JsonElement? Body,
    string? RecordKey,
    bool Retired);

/// <summary>
/// A page of a form type's raw stream. Each document has the shape a single read returns, so a
/// caller that pages the stream and a caller that reads one document see the same thing.
/// </summary>
/// <param name="Documents">The documents after the requested watermark, oldest first.</param>
/// <param name="RawHead">
/// The form type's latest watermark when the page was read. The page never reaches past it: when the
/// last document's watermark equals it, the caller has read everything; when it is lower, the next
/// page starts after that watermark.
/// </param>
public sealed record DocumentPageResponse(IReadOnlyList<StoredDocumentResponse> Documents, long RawHead);
