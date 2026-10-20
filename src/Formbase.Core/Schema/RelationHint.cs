using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>The kind of a declared relation between form types (Formology rules 3·4).</summary>
public enum RelationKind
{
    /// <summary>Rule 3 — the target is a child entity (1:N); the child's <see cref="RelationHint.KeyField"/> points back here.</summary>
    Child,

    /// <summary>Rule 4 — a foreign-key reference to the target; this type's <see cref="RelationHint.KeyField"/> points at it.</summary>
    Reference,
}

/// <summary>
/// A declared relation from one form type to another. Declaration-level: targets are form types,
/// not tables — the proposer resolves the physical table name when it materializes a schema.
/// One form type still projects into one table; a child entity is its own
/// <see cref="FormTypeHints"/>, and this hint carries the link.
/// </summary>
/// <param name="Name">The relation's declared name.</param>
/// <param name="Kind">Whether the target is owned (a child) or merely referenced.</param>
/// <param name="Target">The form type the relation points at.</param>
/// <param name="KeyField">
/// The field carrying the link. For <see cref="RelationKind.Child"/> it is a field of the child that holds
/// the parent's value of the same name.
/// </param>
/// <param name="SectionField">
/// For a <see cref="RelationKind.Child"/> relation only: the field of this form type's documents that holds
/// a repeated section — an array of objects, one per row of the target. When it is given, the target is a
/// <em>section</em> form type: it has no documents of its own, and its rows are derived from this form type's
/// documents when they are projected, one row per array item, with <paramref name="KeyField"/> carrying the
/// parent document's value. Rules a declaration has to keep are in <see cref="DeclaredSections"/>.
/// </param>
public sealed record RelationHint(string Name, RelationKind Kind, FormTypeRef Target, string KeyField, string? SectionField = null);
