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
    /// is safe. Returns the id under which the document was stored.
    /// </summary>
    Task<DocumentId> AcceptAsync(
        FormTypeRef type,
        DocumentBody body,
        DocumentId? idempotencyId = null,
        CancellationToken cancellationToken = default);
}
