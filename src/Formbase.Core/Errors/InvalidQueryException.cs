using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A query named a column the form type's declaration does not have, as a filter or as an ordering
/// key. Distinct from a query that matches nothing: the projection has no column to compare against,
/// so there is no question here to answer. A caller told "no rows" would go looking at their data
/// when what needs fixing is the name they sent.
/// </summary>
/// <remarks>
/// The value side is deliberately not this: a filter whose value does not fit its column names a
/// column that exists and asks something the projection can answer, and the answer is zero rows.
/// </remarks>
public sealed class InvalidQueryException : FormbaseException
{
    public FormTypeRef FormType { get; }

    /// <summary>The names that are not declared columns, in the order the query gave them.</summary>
    public IReadOnlyList<string> UnknownColumns { get; }

    public InvalidQueryException(FormTypeRef formType, IReadOnlyList<string> unknownColumns)
        : base($"Form type '{formType}' declares no column named {Describe(unknownColumns)}; " +
               "a query cannot filter or order by a column that is not declared.")
    {
        FormType = formType;
        UnknownColumns = unknownColumns;
    }

    private static string Describe(IReadOnlyList<string> columns) =>
        string.Join(", ", columns.Select(c => $"'{c}'"));
}
