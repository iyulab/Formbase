namespace Formbase.Core.Query;

/// <summary>
/// One filter condition: a declared column, an operator, and the value to compare against. A query's
/// filters all apply (they are ANDed). A row whose column is null matches no operator but
/// <see cref="FilterOperator.Equal"/> with a null value.
/// </summary>
public sealed record FieldFilter(string Column, FilterOperator Operator, object? Value)
{
    /// <summary>A filter matching rows whose <paramref name="column"/> equals <paramref name="value"/>.</summary>
    public static FieldFilter Equal(string column, object? value) => new(column, FilterOperator.Equal, value);
}
