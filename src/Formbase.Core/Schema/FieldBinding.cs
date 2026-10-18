namespace Formbase.Core.Schema;

/// <summary>
/// The time-binding axis of a declared field (Formology rules 4·5): whether the value is this
/// document's own, a copy fixed at write time, or a reference meant to read true now. Raw-first
/// projection materializes <see cref="Snapshot"/> physically (raw keeps the value written then). A
/// <see cref="Reference"/> is not copied into the row at all: every <c>IProjectionStore</c> computes it
/// when the row is read — in query results, filters, ordering and aggregate groups alike — from the
/// target record as it is now, provided the column's <see cref="EntityRef"/> says how to find that
/// record (a lookup and a via field). A reference declared without that pair cannot be computed; it
/// reads empty and is named in <c>ProjectionResult.UnresolvedReferences</c>.
/// </summary>
public enum FieldBinding
{
    /// <summary>The document's own value (default).</summary>
    Stored,

    /// <summary>True then — the referenced value was copied and fixed at write time.</summary>
    Snapshot,

    /// <summary>
    /// True now — reads the target record's current value, computed by the store each time the row is
    /// read, so a corrected target shows through without re-projecting the referring form type.
    /// Requires a target with a lookup pair; see <see cref="FieldBinding"/>.
    /// </summary>
    Reference,
}
