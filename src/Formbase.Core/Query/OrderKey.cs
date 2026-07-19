namespace Formbase.Core.Query;

/// <summary>
/// One ordering key: a column and its direction. A <see cref="QuerySpec"/> may carry several; they
/// apply in list order (first key primary, the rest as tie-breakers). Ordering is what makes paging
/// deterministic — without a total order, <see cref="QuerySpec.Offset"/>/<see cref="QuerySpec.Limit"/>
/// select an arbitrary slice.
/// </summary>
public sealed record OrderKey(string Column, bool Descending = false);
