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
/// <param name="DeclarationSeq">
/// The declaration the document was accepted under, by its place in the form type's declaration history
/// (<c>GET /formtypes/{type}/declaration/history</c> lists the places; <c>?seq=N</c> reads one): <c>0</c> when no
/// declaration was in force, null when it is not known — the document was stored before the store kept this,
/// or it is a retirement. Stamped once at intake, so a declaration written later does not change it.
/// </param>
public sealed record StoredDocumentResponse(
    Guid DocumentId,
    string FormType,
    long Watermark,
    DateTimeOffset AppendedAt,
    JsonElement? Body,
    string? RecordKey,
    bool Retired,
    int? DeclarationSeq = null);

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

/// <summary>One row of a repeated section, cut from a parent document the way the section's projection cuts it.</summary>
/// <param name="Position">The item's position in the parent's section array, from 0.</param>
/// <param name="Name">
/// What a claim grounded in this row cites: the parent <em>document's</em> id, a slash, and the position — the version of the
/// record the row was cut from. Take it as given; do not put it together from the document id and the position.
/// </param>
/// <param name="Record">The row's record identity as the section's projection names it (<c>fb_record</c>): the parent's record key when it has one, else its document id, a slash, and the position.</param>
/// <param name="Body">The item, with the parent's key value written under the section's key field.</param>
public sealed record SectionRowResponse(int Position, string Name, string Record, JsonElement Body);

/// <summary>A parent document of a page of a section's rows, with the rows it holds.</summary>
/// <param name="DocumentId">Identity of the parent document.</param>
/// <param name="Watermark">Position in the parent's append-only stream.</param>
/// <param name="RecordKey">The record the parent document belongs to, or null for a record of its own.</param>
/// <param name="Retired">Whether the parent document retires <paramref name="RecordKey"/>; it holds no rows.</param>
/// <param name="Rows">The rows the document holds, in array order; empty when it holds none or its section cannot be read.</param>
/// <param name="Refusal">Why the document's section cannot be read (an item that is not a row, a section that is not an array), or null — the section projection skips such a document, so its rows are read nowhere.</param>
public sealed record SectionDocumentResponse(Guid DocumentId, long Watermark, string? RecordKey, bool Retired, IReadOnlyList<SectionRowResponse> Rows, string? Refusal);

/// <summary>
/// A page of a repeated section's rows, read from its parent's raw stream: each parent document with the rows it holds. The
/// watermarks and the head are the parent's — a section has no documents of its own.
/// </summary>
/// <param name="Documents">The parent's documents after the requested watermark, oldest first.</param>
/// <param name="RawHead">The parent stream's head when the page was read; continue until the last watermark received equals it.</param>
public sealed record SectionRowPageResponse(IReadOnlyList<SectionDocumentResponse> Documents, long RawHead);

