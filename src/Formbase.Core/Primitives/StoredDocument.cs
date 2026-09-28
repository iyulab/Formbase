namespace Formbase.Core.Primitives;

/// <summary>
/// A document as it lives in the raw store — the source of truth. Append-only: a correction is a new
/// append (new id, later watermark), never an in-place mutation. A correction names the record it
/// corrects with the same <see cref="Key"/>; the projection then shows only that key's latest document.
/// </summary>
/// <param name="Id">The document's identity, which is also its idempotency key.</param>
/// <param name="Type">The form type the document was accepted under.</param>
/// <param name="Body">
/// The content, verbatim — or <see langword="null"/> for a retirement: the append that takes the record
/// named by <see cref="Key"/> out of the projection while its earlier documents stay in the raw history.
/// A document whose content is the JSON literal <c>null</c> is not a retirement; its body is that value.
/// </param>
/// <param name="Watermark">Position in the append-only stream.</param>
/// <param name="AppendedAt">When the document entered the raw store.</param>
/// <param name="Key">The record this document belongs to, or <see langword="null"/> for a record of its own. Always present on a retirement.</param>
public sealed record StoredDocument(
    DocumentId Id,
    FormTypeRef Type,
    DocumentBody? Body,
    Watermark Watermark,
    DateTimeOffset AppendedAt,
    RecordKey? Key = null)
{
    /// <summary>Whether this append retires the record named by <see cref="Key"/> rather than carrying content.</summary>
    public bool IsRetirement => Body is null;
}
