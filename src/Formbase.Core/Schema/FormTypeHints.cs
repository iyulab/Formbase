using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// The declared field hints for a single form type, plus the physical table name to project into.
/// Absence of a <see cref="FormTypeHints"/> (or an empty field list) means "no projection yet".
/// <paramref name="Relations"/> declares links to other form types (each still its own table —
/// design 2026-07-23 §4), and <paramref name="DeclarationVersion"/> anchors per-row absent-vs-null
/// distinction across schema growth. Defaults reproduce the pre-vocabulary shape.
/// </summary>
public sealed record FormTypeHints(
    FormTypeRef Type,
    string TableName,
    IReadOnlyList<FieldHint> Fields,
    IReadOnlyList<RelationHint>? Relations = null,
    int DeclarationVersion = 1);
