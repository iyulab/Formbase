namespace Formbase.Core.Query;

/// <summary>
/// A minimal record-query specification: equality filters, optional ordering, and optional paging.
/// Kept deliberately thin — richer querying is deferred until a real consumer needs it (YAGNI).
/// <see cref="OrderBy"/> imposes a total order so paging is deterministic; the record read-path always
/// appends the system watermark as a final tie-breaker, so record queries page deterministically even
/// when the caller specifies no ordering.
/// </summary>
public sealed record QuerySpec(
    IReadOnlyDictionary<string, object?>? Filters = null,
    int? Limit = null,
    int? Offset = null,
    IReadOnlyList<OrderKey>? OrderBy = null)
{
    /// <summary>An unfiltered, unordered, unpaged query.</summary>
    public static QuerySpec All { get; } = new();
}
