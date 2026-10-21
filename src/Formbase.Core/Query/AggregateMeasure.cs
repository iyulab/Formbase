namespace Formbase.Core.Query;

/// <summary>
/// What an aggregate measures in each group besides its count: one function over one declared column —
/// a reference column included, read as its computed value. The rows whose column is empty take no
/// part; how many did is reported beside the value (<see cref="MeasureValue.ValueCount"/>).
/// </summary>
/// <param name="Column">The declared column measured.</param>
/// <param name="Function">
/// What is computed. <see cref="MeasureFunction.Sum"/> and <see cref="MeasureFunction.Average"/> apply to
/// <c>Integer</c> and <c>Decimal</c> columns; <see cref="MeasureFunction.Min"/> and
/// <see cref="MeasureFunction.Max"/> also to <c>Timestamp</c>. Text is left out on purpose, as it is for
/// range filters: which of two strings is smaller is a collation choice the stores make differently.
/// <see cref="MeasureFunction.CountDistinct"/> asks only whether two values are equal, so it applies to
/// every type but <c>Jsonb</c>, whose equality the stores read differently (key order, spacing).
/// </param>
public sealed record AggregateMeasure(string Column, MeasureFunction Function)
{
    /// <summary>The sum of <paramref name="column"/> in each group.</summary>
    public static AggregateMeasure Sum(string column) => new(column, MeasureFunction.Sum);

    /// <summary>The average of <paramref name="column"/> in each group.</summary>
    public static AggregateMeasure Average(string column) => new(column, MeasureFunction.Average);

    /// <summary>The smallest value of <paramref name="column"/> in each group.</summary>
    public static AggregateMeasure Min(string column) => new(column, MeasureFunction.Min);

    /// <summary>How many different values <paramref name="column"/> holds in each group.</summary>
    public static AggregateMeasure CountDistinct(string column) => new(column, MeasureFunction.CountDistinct);

    /// <summary>The largest value of <paramref name="column"/> in each group.</summary>
    public static AggregateMeasure Max(string column) => new(column, MeasureFunction.Max);
}

/// <summary>The function an <see cref="AggregateMeasure"/> computes over a group's values.</summary>
public enum MeasureFunction
{
    Sum,
    Average,
    Min,
    Max,

    /// <summary>
    /// How many different values the group's rows hold in the column — empty ones not counted. The
    /// value is a <see cref="long"/>, and <c>0</c> rather than null when no row has a value: a count of
    /// nothing is zero, as <see cref="AggregateGroup.Count"/> is.
    /// </summary>
    CountDistinct,
}

/// <summary>
/// One measure of one group.
/// </summary>
/// <param name="Measure">The measure asked for.</param>
/// <param name="Value">
/// The result, or null when no row of the group has a value in the column — never a zero standing in
/// for "nothing to add". A sum or an average is a <see cref="decimal"/> whatever the column's numeric
/// type, so the same question gets the same type from every store; a minimum or maximum is a value of
/// the column's own type.
/// </param>
/// <param name="ValueCount">
/// How many of the group's rows have a value in the column — the rows <see cref="Value"/> is computed
/// from. Fewer than <see cref="AggregateGroup.Count"/> means the rest are empty there (never declared
/// in the document, or not convertible to the column's type when projected) and took no part.
/// </param>
public sealed record MeasureValue(AggregateMeasure Measure, object? Value, long ValueCount);
