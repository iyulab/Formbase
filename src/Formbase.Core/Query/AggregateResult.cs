using Formbase.Core.Primitives;

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
/// <param name="Key">The value of each grouping column.</param>
/// <param name="Count">How many records the group has.</param>
/// <param name="Documents">
/// The documents the count is made of — each record's current document, in the order they were
/// accepted — when the aggregate asked for them (<see cref="AggregateSpec.DocumentsPerGroup"/>), else
/// null. Fewer than <paramref name="Count"/> means the list stopped at that limit;
/// <see cref="AggregateSpec.RecordsOf"/> reads the rest.
/// </param>
public sealed record AggregateGroup(IReadOnlyDictionary<string, object?> Key, long Count, IReadOnlyList<DocumentId>? Documents = null);
