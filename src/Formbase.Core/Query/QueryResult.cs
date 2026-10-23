using Formbase.Core.Primitives;

namespace Formbase.Core.Query;

/// <summary>
/// Result of a record query against a projected table. <see cref="Stale"/> flags that the
/// projection was current-but-behind the raw head when read; <see cref="SkippedDocuments"/> that the
/// answer is over fewer documents than were accepted.
/// </summary>
/// <param name="Rows">The records the query kept.</param>
/// <param name="Stale">Whether a table the answer read was behind the raw head, or read a target that was.</param>
public sealed record QueryResult(
    IReadOnlyList<RecordRow> Rows,
    bool Stale)
{
    /// <summary>
    /// How many documents a table the answer read could not project — the form type's own table, and the
    /// table of each child relation a <see cref="QuerySpec.Children"/> filter read — each counted once, as the
    /// last projection of each table recorded them (<c>GetProjectionSkipsAsync</c> names them and why). Zero
    /// means every accepted document stands in those tables. Otherwise the answer is not over all of them: a
    /// document missing from the form type's table is in no row, and a parent whose section could not be read
    /// has no rows there — so a child filter that keeps rows with none (<see cref="ChildQuantifier.None"/>)
    /// keeps it, and one that keeps rows with some cannot find it.
    /// </summary>
    public int SkippedDocuments { get; init; }
}

/// <summary>
/// One record read from a projection: which record it is, and its declared fields.
/// </summary>
/// <param name="Record">
/// Which record the row is — the same identity an aggregate names as its evidence
/// (<see cref="AggregateGroup.Records"/>), so a count and the rows behind it can be matched up.
/// </param>
/// <param name="Fields">
/// Exactly the declared columns, each by its declared name. The projection's own bookkeeping is not
/// among them: the record's identity is carried by <see cref="Record"/>, and anything else the backing
/// store keeps is not the caller's to depend on.
/// </param>
/// <param name="References">
/// Where each reference field's value was read from, by field name — present when the query asked
/// for it (<see cref="QuerySpec.ReferenceSources"/>), else null.
/// </param>
public sealed record RecordRow(
    RecordRef Record,
    IReadOnlyDictionary<string, object?> Fields,
    IReadOnlyDictionary<string, ReferenceSource>? References = null);
