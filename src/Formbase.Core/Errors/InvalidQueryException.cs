using Formbase.Core.Primitives;
using Formbase.Core.Query;

namespace Formbase.Core.Errors;

/// <summary>
/// A query asked something the form type's declaration cannot answer: it named a column the
/// declaration does not have (as a filter, an ordering key, a grouping column or a measured column), or
/// it applied an operator or a measure to a column whose type it does not apply to. Distinct from a query that matches
/// nothing: there is no question here to answer. A caller told "no rows" would go looking at their data
/// when what needs fixing is the query they sent.
/// </summary>
/// <remarks>
/// The value side is deliberately not this: a filter whose value does not fit its column names a
/// column that exists and asks something the projection can answer, and the answer is zero rows. The
/// one exception is a missing value for an operator that compares: "greater than nothing" is not a
/// question with an answer.
/// </remarks>
public sealed class InvalidQueryException : FormbaseException
{
    public FormTypeRef FormType { get; }

    /// <summary>The names that are not declared columns, in the order the query gave them.</summary>
    public IReadOnlyList<string> UnknownColumns { get; }

    /// <summary>
    /// Filters on declared columns whose operator does not apply to the column's type, or that compare
    /// against a null value, in the order the query gave them.
    /// </summary>
    public IReadOnlyList<FieldFilter> InapplicableFilters { get; }

    /// <summary>
    /// Measures of declared columns whose function does not apply to the column's type, in the order the
    /// aggregate gave them.
    /// </summary>
    public IReadOnlyList<AggregateMeasure> InapplicableMeasures { get; }

    public InvalidQueryException(FormTypeRef formType, IReadOnlyList<string> unknownColumns)
        : this(formType, unknownColumns, [])
    {
    }

    public InvalidQueryException(
        FormTypeRef formType,
        IReadOnlyList<string> unknownColumns,
        IReadOnlyList<FieldFilter> inapplicableFilters)
        : this(formType, unknownColumns, inapplicableFilters, [])
    {
    }

    public InvalidQueryException(
        FormTypeRef formType,
        IReadOnlyList<string> unknownColumns,
        IReadOnlyList<FieldFilter> inapplicableFilters,
        IReadOnlyList<AggregateMeasure> inapplicableMeasures)
        : base(Describe(formType, unknownColumns, inapplicableFilters, inapplicableMeasures))
    {
        FormType = formType;
        UnknownColumns = unknownColumns;
        InapplicableFilters = inapplicableFilters;
        InapplicableMeasures = inapplicableMeasures;
    }

    private static string Describe(
        FormTypeRef formType,
        IReadOnlyList<string> unknownColumns,
        IReadOnlyList<FieldFilter> inapplicable,
        IReadOnlyList<AggregateMeasure> inapplicableMeasures)
    {
        var parts = new List<string>(3);
        if (unknownColumns.Count > 0)
        {
            parts.Add($"Form type '{formType}' declares no column named " +
                      string.Join(", ", unknownColumns.Select(c => $"'{c}'")) +
                      "; a query cannot filter, order, group by or measure a column that is not declared.");
        }

        if (inapplicable.Count > 0)
        {
            parts.Add("A filter's operator must apply to its column's type and compare against a value: " +
                      string.Join(", ", inapplicable.Select(f => $"'{f.Column}' {f.Operator}" +
                                                                 (f.Value is null ? " null" : string.Empty))) +
                      $" cannot be asked of form type '{formType}'.");
        }

        if (inapplicableMeasures.Count > 0)
        {
            parts.Add("A sum or an average applies to an integer or decimal column, a minimum or maximum also to a timestamp: " +
                      string.Join(", ", inapplicableMeasures.Select(m => $"{m.Function} of '{m.Column}'")) +
                      $" cannot be asked of form type '{formType}'.");
        }

        return string.Join(" ", parts);
    }
}
