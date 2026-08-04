using System.Text.Json;

namespace Formbase.Host.Contracts;

/// <summary>
/// What intake answers: the document's identity, which is also the idempotency key for any repeat
/// of the same submission.
/// </summary>
/// <param name="DocumentId">Identity of the stored document.</param>
/// <param name="FormType">The form type the document was accepted under.</param>
public sealed record AcceptedDocumentResponse(Guid DocumentId, string FormType);

/// <summary>
/// A document as the raw store holds it. The body is returned as it was sent — the store keeps it
/// verbatim, and reinterpreting it on the way out would make the reply a different document from
/// the one that was accepted.
/// </summary>
/// <param name="DocumentId">Identity of the stored document.</param>
/// <param name="FormType">The form type the document was accepted under.</param>
/// <param name="Watermark">Position in the form type's append-only stream.</param>
/// <param name="AppendedAt">When the document entered the raw store.</param>
/// <param name="Body">The document content, verbatim.</param>
public sealed record StoredDocumentResponse(
    Guid DocumentId,
    string FormType,
    long Watermark,
    DateTimeOffset AppendedAt,
    JsonElement Body);
