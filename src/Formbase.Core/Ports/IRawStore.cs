using Formbase.Core.Primitives;

namespace Formbase.Core.Ports;

/// <summary>
/// The source of truth. Append-only store of documents, owned by formbase. Depends on nothing
/// else in the engine. A correction is a new append, never an update.
/// </summary>
public interface IRawStore
{
    /// <summary>
    /// Appends a document under <paramref name="id"/> (adapter-supplied for idempotency) and
    /// returns the stored form, including its assigned watermark. Appending the same id twice
    /// is idempotent — the original stored document is returned unchanged.
    /// </summary>
    Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default);

    /// <summary>Fetches a single document by id — the "show me this document" path. Null if absent.</summary>
    Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default);

    /// <summary>Streams a form type's documents in append order after <paramref name="after"/> — the projection scan.</summary>
    IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, CancellationToken cancellationToken = default);

    /// <summary>The latest watermark for a form type, or <see cref="Watermark.Zero"/> if none.</summary>
    Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}
