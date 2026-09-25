using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// An idempotency key was sent for a document of one form type while it already identifies a
/// document of another. That is not a retry — a retry repeats the request that was accepted — so
/// answering it as one would report a document the caller never stored and drop the one it sent.
/// The document already held under the key is unchanged.
/// </summary>
public sealed class IdempotencyKeyReusedException : FormbaseException
{
    /// <summary>The key, which is also the id of the document already stored under it.</summary>
    public DocumentId DocumentId { get; }

    /// <summary>The form type this request named.</summary>
    public FormTypeRef RequestedType { get; }

    /// <summary>The form type of the document the key already identifies.</summary>
    public FormTypeRef StoredType { get; }

    public IdempotencyKeyReusedException(DocumentId documentId, FormTypeRef requestedType, FormTypeRef storedType)
        : base($"Idempotency key '{documentId.Value}' already identifies a document of form type '{storedType}', " +
               $"so it cannot be used for a document of form type '{requestedType}'. A key belongs to one request; " +
               "send a new key for a new document.")
    {
        DocumentId = documentId;
        RequestedType = requestedType;
        StoredType = storedType;
    }
}
