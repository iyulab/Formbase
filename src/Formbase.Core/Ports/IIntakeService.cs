using Formbase.Core.Primitives;

namespace Formbase.Core.Ports;

/// <summary>
/// Accepts documents into the raw store. Declaration is never required to accept a document —
/// this is the raw-first intake path. Success means the data is durable, regardless of whether
/// a projection exists.
/// </summary>
public interface IIntakeService
{
    /// <summary>
    /// Accepts a document of the given form type. A first-seen form type is auto-registered
    /// (type only, not a schema). If <paramref name="idempotencyId"/> is supplied, re-submission
    /// is safe: a key already holding a document of this form type returns that document's id without
    /// storing a second one. A key already holding a document of another form type is refused with
    /// <see cref="Errors.IdempotencyKeyReusedException"/>. Returns the id under which the document was stored.
    /// </summary>
    Task<DocumentId> AcceptAsync(
        FormTypeRef type,
        DocumentBody body,
        DocumentId? idempotencyId = null,
        CancellationToken cancellationToken = default);
}
