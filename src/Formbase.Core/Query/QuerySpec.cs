namespace Formbase.Core.Query;

/// <summary>
/// A record-query specification: filters, optional ordering, and optional paging.
/// <see cref="OrderBy"/> imposes a total order so paging is deterministic; the record read-path always
/// appends the system watermark as a final tie-breaker, so record queries page deterministically even
/// when the caller specifies no ordering.
/// </summary>
/// <param name="Filters">Every filter applies; none means every row.</param>
/// <param name="Limit">The most rows to return; none means all.</param>
/// <param name="Offset">The rows to skip, in the query's order, before the first returned.</param>
/// <param name="OrderBy">The order, by column; the watermark is appended as the final key.</param>
/// <param name="ReferenceSources">
/// When true, each row also says where each of its reference fields was read from
/// (<see cref="RecordRow.References"/>) — the target record and what reading it found, in the same
/// read as the value. Off by default: only a caller that asks pays for it.
/// </param>
public sealed record QuerySpec(
    IReadOnlyList<FieldFilter>? Filters = null,
    int? Limit = null,
    int? Offset = null,
    IReadOnlyList<OrderKey>? OrderBy = null,
    bool ReferenceSources = false)
{
    /// <summary>An unfiltered, unordered, unpaged query.</summary>
    public static QuerySpec All { get; } = new();
}
