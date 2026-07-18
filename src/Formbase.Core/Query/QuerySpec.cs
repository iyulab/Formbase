namespace Formbase.Core.Query;

/// <summary>
/// A minimal record-query specification: equality filters plus optional paging.
/// Kept deliberately thin — richer querying is deferred until a real consumer needs it (YAGNI).
/// </summary>
public sealed record QuerySpec(
    IReadOnlyDictionary<string, object?>? Filters = null,
    int? Limit = null,
    int? Offset = null)
{
    /// <summary>An unfiltered, unpaged query.</summary>
    public static QuerySpec All { get; } = new();
}
