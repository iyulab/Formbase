using Formbase.Core.Primitives;

namespace Formbase.Core.Query;

/// <summary>
/// The answer to an <see cref="AggregateSpec"/>. <see cref="Groups"/> are ordered by their key columns
/// in <see cref="AggregateSpec.GroupBy"/> order, nulls first, so the same question gets the same list.
/// <see cref="Stale"/> flags that the projection was current-but-behind the raw head when read;
/// <see cref="SkippedDocuments"/> counts the documents the tables it read could not project, as
/// <see cref="QueryResult.SkippedDocuments"/> does — non-zero, the counts are not over every accepted document.
/// </summary>
public sealed record AggregateResult(IReadOnlyList<AggregateGroup> Groups, bool Stale)
{
    /// <summary>
    /// How many documents the tables the aggregate read could not project, counted once — see
    /// <see cref="QueryResult.SkippedDocuments"/>. Non-zero, the counts are not over every accepted document.
    /// </summary>
    public int SkippedDocuments { get; init; }
}

/// <summary>
/// One group: the value of each grouping column (empty when the aggregate is ungrouped), how many
/// records carry it, and what the aggregate measured over them.
/// </summary>
/// <param name="Key">The value of each grouping column.</param>
/// <param name="Count">How many records the group has.</param>
/// <param name="Records">
/// The records the count is made of — each one as its current document and its key, in the order
/// their documents were accepted — when the aggregate asked for them
/// (<see cref="AggregateSpec.RecordsPerGroup"/>), else null. They are the same identities the rows of
/// <see cref="AggregateSpec.RecordsOf"/> carry (<see cref="RecordRow.Record"/>), so the evidence for a
/// count can be matched to the rows that make it up. Fewer than <paramref name="Count"/> means the list
/// stopped at that limit; <see cref="AggregateSpec.RecordsOf"/> reads the rest.
/// </param>
/// <param name="Measures">
/// One value per <see cref="AggregateSpec.Measures"/> entry, in the order the aggregate asked for them;
/// empty when it asked for none. The records the values are computed from are the group's records —
/// <see cref="Records"/> when asked for, <see cref="AggregateSpec.RecordsOf"/> otherwise.
/// </param>
public sealed record AggregateGroup(
    IReadOnlyDictionary<string, object?> Key,
    long Count,
    IReadOnlyList<RecordRef>? Records = null,
    IReadOnlyList<MeasureValue>? Measures = null)
{
    /// <summary>The measures, empty rather than null when the aggregate asked for none.</summary>
    public IReadOnlyList<MeasureValue> Measures { get; init; } = Measures ?? [];
}
