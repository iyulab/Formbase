using Formbase.Core.Primitives;

namespace Formbase.Core.Ports;

/// <summary>
/// The source of truth. Append-only store of documents, owned by formbase. Depends on nothing
/// else in the engine. A correction is a new append, never an update — it names the record it
/// corrects with a <see cref="RecordKey"/>, and a retirement is an append too.
/// </summary>
public interface IRawStore
{
    /// <summary>
    /// Appends a document under <paramref name="id"/> (adapter-supplied for idempotency) and
    /// returns the stored form, including its assigned watermark. <paramref name="key"/>, when given,
    /// names the record the document belongs to (see <see cref="RecordKey"/>). Appending the same id
    /// twice is idempotent — the original stored document is returned unchanged, whatever this call
    /// carried.
    /// </summary>
    Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, RecordKey? key = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends a retirement of the record <paramref name="key"/> under <paramref name="id"/>: a document
    /// with no body that takes the record out of the projection while its earlier documents stay in the
    /// raw history. Nothing checks that the key was ever used — a retirement is an append like any other.
    /// Idempotent by id, as <see cref="AppendAsync"/> is.
    /// </summary>
    Task<StoredDocument> RetireAsync(FormTypeRef type, DocumentId id, RecordKey key, CancellationToken cancellationToken = default);

    /// <summary>
    /// Appends <paramref name="appends"/> under <paramref name="type"/> as one unit: when this returns,
    /// every append is durable; when it throws, none of them was stored and no watermark was taken. The
    /// new appends take consecutive watermarks in the order given, with no other append between them.
    /// Returns one stored document per element of <paramref name="appends"/>, in the same order.
    /// <para>
    /// An id is still the idempotency key, but a batch is stricter than <see cref="AppendAsync"/>: an id
    /// already held by the same request (<see cref="RawAppend.Repeats"/>) returns the document held, taking
    /// no new watermark — so a retried batch stores nothing twice — while an id held by, or repeated
    /// within the batch with, another request refuses the whole batch with
    /// <see cref="Errors.IdempotencyKeyReusedException"/>. The check has to happen inside the store:
    /// found after a commit, it would leave the rest of the batch stored under a call that failed.
    /// <see cref="RawAppend.Distinct"/> and <see cref="RawAppend.EnsureRepeats"/> carry the rule for an
    /// implementation. An empty batch stores nothing.
    /// </para>
    /// </summary>
    Task<IReadOnlyList<StoredDocument>> AppendManyAsync(FormTypeRef type, IReadOnlyList<RawAppend> appends, CancellationToken cancellationToken = default);

    /// <summary>Fetches a single document by id — the "show me this document" path. Null if absent.</summary>
    Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Streams a form type's documents in append order after <paramref name="after"/> — the projection
    /// scan. Every append comes back, corrections and retirements included: folding them into records is
    /// the reader's step (<see cref="Projection.RecordFold"/>), because the raw history is the point.
    /// </summary>
    IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, CancellationToken cancellationToken = default);

    /// <summary>The latest watermark for a form type, or <see cref="Watermark.Zero"/> if none.</summary>
    Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}
