namespace Formbase.Core.Query;

/// <summary>
/// The answer to an <see cref="AggregateSpec"/>. <see cref="Groups"/> are ordered by their key columns
/// in <see cref="AggregateSpec.GroupBy"/> order, nulls first, so the same question gets the same list.
/// <see cref="Stale"/> flags that the projection was current-but-behind the raw head when read.
/// </summary>
public sealed record AggregateResult(IReadOnlyList<AggregateGroup> Groups, bool Stale);

/// <summary>
/// One group: the value of each grouping column (empty when the aggregate is ungrouped) and how many
/// records carry it.
/// </summary>
public sealed record AggregateGroup(IReadOnlyDictionary<string, object?> Key, long Count);
