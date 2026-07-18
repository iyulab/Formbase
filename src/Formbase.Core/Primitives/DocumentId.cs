namespace Formbase.Core.Primitives;

/// <summary>
/// Identity of a stored document. Adapters may supply a client-generated id so that
/// re-submission after an intake failure is idempotent (same id → same raw row).
/// </summary>
public readonly record struct DocumentId(Guid Value)
{
    /// <summary>Generates a fresh document id.</summary>
    public static DocumentId New() => new(Guid.NewGuid());

    /// <summary>Wraps a caller-supplied id (idempotency key).</summary>
    public static DocumentId From(Guid value) => new(value);

    public override string ToString() => Value.ToString();
}
