namespace Formbase.Core.Query;

/// <summary>
/// An aggregate over a form type's projected records: the rows the <see cref="Filters"/> keep, counted
/// — and, for each of the <see cref="Measures"/>, summed, averaged or bounded — per distinct combination
/// of the <see cref="GroupBy"/> columns. Without grouping the answer is one group — every kept row, zero
/// included.
/// </summary>
/// <param name="GroupBy">The columns whose distinct combinations are the groups — any declared column but a <c>Jsonb</c> one, whose equality the stores read differently.</param>
/// <param name="Filters">The rows counted.</param>
/// <param name="RecordsPerGroup">
/// When set, each group also carries the records its count is made of
/// (<see cref="AggregateGroup.Records"/>) — at most this many, in the order their documents were
/// accepted, read in the same read as the count so the two cannot disagree. Must be positive.
/// </param>
/// <param name="Measures">
/// What each group measures besides its count (<see cref="AggregateGroup.Measures"/>, in this order),
/// read in the same read as the count. A measure of an undeclared column, or of a column whose type its
/// function does not apply to, is refused rather than answered empty.
/// </param>
/// <param name="OrderBy">
/// How the groups are ordered (<see cref="GroupOrder"/>) — by count, by a grouping column or by a measure,
/// most significant first. Without it, groups come in the order of their grouping columns, nulls first.
/// A key on a column that is not grouped, or on a measure that is not asked for, is refused.
/// </param>
/// <param name="Limit">
/// When set, only the first this many groups in that order are returned — the «top N» of a ranking.
/// Must be positive.
/// </param>
/// <param name="Children">Conditions on child relations (<see cref="ChildFilter"/>) the counted rows also meet.</param>
public sealed record AggregateSpec(
    IReadOnlyList<string>? GroupBy = null,
    IReadOnlyList<FieldFilter>? Filters = null,
    int? RecordsPerGroup = null,
    IReadOnlyList<AggregateMeasure>? Measures = null,
    IReadOnlyList<GroupOrder>? OrderBy = null,
    int? Limit = null,
    IReadOnlyList<ChildFilter>? Children = null)
{
    /// <summary>The count of every record.</summary>
    public static AggregateSpec CountAll { get; } = new();

    /// <summary>
    /// The query that reads every record behind <paramref name="group"/>: this aggregate's filters plus
    /// each grouping column equal to the group's value — an empty value as an empty column — read in the
    /// order the documents were accepted, as a record query without an order is. It reaches past <see cref="RecordsPerGroup"/>; it is a read
    /// of its own, so a projection that moved between the two can answer it differently.
    /// </summary>
    public QuerySpec RecordsOf(AggregateGroup group)
    {
        ArgumentNullException.ThrowIfNull(group);

        var filters = new List<FieldFilter>(Filters ?? []);
        foreach (var column in GroupBy ?? [])
        {
            var value = group.Key.TryGetValue(column, out var held) ? held : null;
            filters.Add(value is null
                ? new FieldFilter(column, FilterOperator.IsNull, null)
                : new FieldFilter(column, FilterOperator.Equal, value));
        }

        return new QuerySpec(Filters: filters);
    }
}
