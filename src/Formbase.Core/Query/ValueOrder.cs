namespace Formbase.Core.Query;

/// <summary>
/// The one ordering of column values the core uses wherever it sorts: nulls first, then values of the
/// same type in their natural order. Values of different types never meet in one column of a
/// projection; if they do, they are kept apart by type name so the order stays total instead of
/// throwing.
/// </summary>
internal static class ValueOrder
{
    public static IComparer<object?> Comparer { get; } = Comparer<object?>.Create(Compare);

    public static int Compare(object? a, object? b)
    {
        if (a is null)
        {
            return b is null ? 0 : -1;
        }

        if (b is null)
        {
            return 1;
        }

        return a.GetType() == b.GetType()
            ? Comparer<object>.Default.Compare(a, b)
            : string.CompareOrdinal(a.GetType().FullName, b.GetType().FullName);
    }
}
