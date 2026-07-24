namespace Formbase.Core.Schema;

/// <summary>
/// The time-binding axis of a declared field (Formology rules 4·5): whether the value is this
/// document's own, a copy fixed at write time, or a reference meant to read true now. Stage-1
/// semantics preserve and deliver the declaration; raw-first projection already materializes
/// <see cref="Snapshot"/> physically (raw keeps the value written then), while
/// <see cref="Reference"/> resolution is a query-layer concern and is not executed yet.
/// </summary>
public enum FieldBinding
{
    /// <summary>The document's own value (default).</summary>
    Stored,

    /// <summary>True then — the referenced value was copied and fixed at write time.</summary>
    Snapshot,

    /// <summary>True now — reads the target's current value; declared but not yet resolved by the engine.</summary>
    Reference,
}
