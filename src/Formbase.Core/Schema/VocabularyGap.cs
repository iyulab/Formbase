namespace Formbase.Core.Schema;

/// <summary>
/// Which axis of the declaration vocabulary a dropped construct demands. An input adapter that
/// translates a richer declaration language into <see cref="FormTypeHints"/> reports every construct
/// it cannot carry as one of these kinds: relations (Formology rule 4), time binding (rule 5), the
/// identity/display split, derived fields, declared value constraints, and constructs the adapter
/// deliberately leaves unresolved.
/// </summary>
public enum VocabularyGapKind
{
    /// <summary>A foreign-key style reference to another entity (Formology rule 4 — true now).</summary>
    Relation,

    /// <summary>Hard/soft binding mode on a reference (Formology rule 5 — fixed then vs true now).</summary>
    TimeBinding,

    /// <summary>A display label distinct from the extraction name (identity/display split).</summary>
    IdentityDisplay,

    /// <summary>
    /// A rollup or computed field — derived, not stored raw, and not carried — or a lookup the declaration
    /// does not say enough to declare as a computed reference (the detail says what is missing).
    /// </summary>
    Derived,

    /// <summary>A declared value constraint (a value set, a range, a length, a pattern, uniqueness) with no hint slot.</summary>
    Constraint,

    /// <summary>A construct the adapter does not attempt to resolve (inheritance, owners, element types).</summary>
    Unresolved,

    /// <summary>
    /// A soft (true-now) binding: it names its target column but not which target record, so the
    /// reference is declared without a lookup pair and is stored, not computed. A lookup field over a
    /// reference says both and is computed.
    /// </summary>
    ReferenceLookup,
}

/// <summary>
/// One construct of a source declaration that the <see cref="FieldHint"/> vocabulary cannot carry,
/// reported by the input adapter that dropped it. An adapter is lossy on purpose: the gap list makes
/// the loss visible and countable instead of silent.
/// </summary>
/// <param name="Declaration">The declaration the construct belongs to, as the source names it (a model, a form).</param>
/// <param name="Field">The field it belongs to, or null for a declaration-level construct.</param>
/// <param name="Kind">The vocabulary axis it would need.</param>
/// <param name="Construct">The construct as the source wrote it.</param>
/// <param name="Detail">Why it drops, and what would carry it.</param>
public sealed record VocabularyGap(
    string Declaration,
    string? Field,
    VocabularyGapKind Kind,
    string Construct,
    string Detail);
