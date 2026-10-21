namespace Formbase.Core.Query;

/// <summary>
/// One key an aggregate's groups are ordered by (<see cref="AggregateSpec.OrderBy"/>): a group's count, the
/// value of one of its grouping columns, or one of its measures. Keys apply in list order; groups still
/// tied are ordered by their grouping columns, so the order is total and the same from every store. An
/// empty value — a null key, a measure with nothing to measure — sorts first, last when descending, as
/// it does in a record query.
/// </summary>
public sealed record GroupOrder
{
    private GroupOrder(GroupOrderTarget target, string? column, int? measure, bool descending)
    {
        Target = target;
        Column = column;
        Measure = measure;
        Descending = descending;
    }

    /// <summary>What the key reads.</summary>
    public GroupOrderTarget Target { get; }

    /// <summary>The grouping column, when <see cref="Target"/> is <see cref="GroupOrderTarget.Key"/>.</summary>
    public string? Column { get; }

    /// <summary>
    /// The position of the measure in <see cref="AggregateSpec.Measures"/>, when <see cref="Target"/> is
    /// <see cref="GroupOrderTarget.Measure"/> — a position rather than a column, since one column may be
    /// measured twice.
    /// </summary>
    public int? Measure { get; }

    /// <summary>Whether larger values come first.</summary>
    public bool Descending { get; }

    /// <summary>By how many rows each group holds.</summary>
    public static GroupOrder ByCount(bool descending = false) => new(GroupOrderTarget.Count, null, null, descending);

    /// <summary>By the value of the grouping column <paramref name="column"/>.</summary>
    public static GroupOrder ByKey(string column, bool descending = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(column);
        return new(GroupOrderTarget.Key, column, null, descending);
    }

    /// <summary>By the value of the measure at <paramref name="index"/> in <see cref="AggregateSpec.Measures"/>.</summary>
    public static GroupOrder ByMeasure(int index, bool descending = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        return new(GroupOrderTarget.Measure, null, index, descending);
    }
}

/// <summary>What a <see cref="GroupOrder"/> reads from each group.</summary>
public enum GroupOrderTarget
{
    /// <summary><see cref="AggregateGroup.Count"/>.</summary>
    Count,

    /// <summary>A value of <see cref="AggregateGroup.Key"/>.</summary>
    Key,

    /// <summary>A value of <see cref="AggregateGroup.Measures"/>.</summary>
    Measure,
}
