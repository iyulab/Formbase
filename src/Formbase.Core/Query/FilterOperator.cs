namespace Formbase.Core.Query;

/// <summary>
/// How a <see cref="FieldFilter"/> compares a column with its value. Each operator is one a backing
/// store answers natively; which column types an operator applies to is part of the contract and is
/// checked before a store is asked (<see cref="Errors.InvalidQueryException"/>).
/// </summary>
public enum FilterOperator
{
    /// <summary>The column equals the value. Applies to every column type.</summary>
    Equal,

    /// <summary>The column is greater than the value. Integer, decimal and timestamp columns.</summary>
    GreaterThan,

    /// <summary>The column is greater than or equal to the value. Integer, decimal and timestamp columns.</summary>
    GreaterThanOrEqual,

    /// <summary>The column is less than the value. Integer, decimal and timestamp columns.</summary>
    LessThan,

    /// <summary>The column is less than or equal to the value. Integer, decimal and timestamp columns.</summary>
    LessThanOrEqual,

    /// <summary>The column contains the value, ignoring case. Text columns.</summary>
    Contains,

    /// <summary>The column starts with the value, ignoring case. Text columns.</summary>
    StartsWith,
}
