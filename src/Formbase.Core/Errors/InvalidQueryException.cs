using Formbase.Core.Primitives;
using Formbase.Core.Query;

namespace Formbase.Core.Errors;

/// <summary>
/// A query asked something the form type's declaration cannot answer: it named a column the
/// declaration does not have (as a filter, an ordering key or a grouping column), or it applied an
/// operator to a column whose type the operator does not compare. Distinct from a query that matches
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

    public InvalidQueryException(FormTypeRef formType, IReadOnlyList<string> unknownColumns)
        : this(formType, unknownColumns, [])
    {
    }

    public InvalidQueryException(
        FormTypeRef formType,
        IReadOnlyList<string> unknownColumns,
        IReadOnlyList<FieldFilter> inapplicableFilters)
        : base(Describe(formType, unknownColumns, inapplicableFilters))
    {
        FormType = formType;
        UnknownColumns = unknownColumns;
        InapplicableFilters = inapplicableFilters;
    }

    private static string Describe(
        FormTypeRef formType, IReadOnlyList<string> unknownColumns, IReadOnlyList<FieldFilter> inapplicable)
    {
        var parts = new List<string>(2);
        if (unknownColumns.Count > 0)
        {
            parts.Add($"Form type '{formType}' declares no column named " +
                      string.Join(", ", unknownColumns.Select(c => $"'{c}'")) +
                      "; a query cannot filter, order or group by a column that is not declared.");
        }

        if (inapplicable.Count > 0)
        {
            parts.Add("A filter's operator must apply to its column's type and compare against a value: " +
                      string.Join(", ", inapplicable.Select(f => $"'{f.Column}' {f.Operator}" +
                                                                 (f.Value is null ? " null" : string.Empty))) +
                      $" cannot be asked of form type '{formType}'.");
        }

        return string.Join(" ", parts);
    }
}
