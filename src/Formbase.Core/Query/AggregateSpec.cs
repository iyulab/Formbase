namespace Formbase.Core.Query;

/// <summary>
/// An aggregate over a form type's projected records: the rows the <see cref="Filters"/> keep, counted
/// per distinct combination of the <see cref="GroupBy"/> columns. Without grouping the answer is one
/// group — the count of every kept row, zero included.
/// </summary>
public sealed record AggregateSpec(
    IReadOnlyList<string>? GroupBy = null,
    IReadOnlyList<FieldFilter>? Filters = null)
{
    /// <summary>The count of every record.</summary>
    public static AggregateSpec CountAll { get; } = new();
}
