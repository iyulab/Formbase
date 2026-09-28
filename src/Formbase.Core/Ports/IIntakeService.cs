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
    /// (type only, not a schema). <paramref name="recordKey"/>, when given, names the record the document
    /// belongs to: a later document under the same key corrects it, and the projection shows only the
    /// latest (see <see cref="RecordKey"/>). If <paramref name="idempotencyId"/> is supplied, re-submission
    /// is safe: a key already holding this request — this form type, this record key, and a body equal as
    /// a JSON value — returns that document's id without storing a second one. A key already holding
    /// another request is refused with <see cref="Errors.IdempotencyKeyReusedException"/>. Returns the id
    /// under which the document was stored.
    /// </summary>
    Task<DocumentId> AcceptAsync(
        FormTypeRef type,
        DocumentBody body,
        DocumentId? idempotencyId = null,
        RecordKey? recordKey = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retires the record <paramref name="recordKey"/>: appends a document with no body, which takes the
    /// record out of the projection while its earlier documents stay in the raw history. Appending under
    /// the key again brings the record back. Nothing checks that the key was ever used. Idempotency works
    /// as in <see cref="AcceptAsync"/> — a key already holding this retirement is a retry, a key holding
    /// anything else is refused. Returns the id of the retirement.
    /// </summary>
    Task<DocumentId> RetireAsync(
        FormTypeRef type,
        RecordKey recordKey,
        DocumentId? idempotencyId = null,
        CancellationToken cancellationToken = default);
}
