using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>The kind of a declared relation between form types (Formology rules 3·4).</summary>
public enum RelationKind
{
    /// <summary>Rule 3 — the target is a child entity (1:N); the child's <paramref name="KeyField"/> points back here.</summary>
    Child,

    /// <summary>Rule 4 — a foreign-key reference to the target; this type's <paramref name="KeyField"/> points at it.</summary>
    Reference,
}

/// <summary>
/// A declared relation from one form type to another. Declaration-level: targets are form types,
/// not tables — the proposer resolves the physical table name when it materializes a schema.
/// One form type still projects into one table; a child entity is its own
/// <see cref="FormTypeHints"/>, and this hint carries only the link (design 2026-07-23 §4).
/// </summary>
public sealed record RelationHint(string Name, RelationKind Kind, FormTypeRef Target, string KeyField);
