namespace Formbase.Core.Query;

/// <summary>
/// A condition on a child relation, read as OData's lambda operators read one. With
/// <see cref="ChildQuantifier.Any"/>, the parent rows that have at least one row of the relation's target the
/// condition keeps — «the inspections with a dent checked», «the orders with a line over 100» (<c>any()</c>);
/// with <see cref="ChildQuantifier.None"/>, the parent rows that have none — «the inspections with no defect
/// checked» (<c>not any()</c>). Either asks about existence, so a parent with several matching rows is still
/// one row. Without a condition every row of the target counts: <c>Any</c> keeps the parents with a row,
/// <c>None</c> the parents with no row at all — «the documents with no option checked», where each checked
/// option is a row of a repeated section. A query's child filters all apply, each about its own relation,
/// alongside its field filters.
/// </summary>
/// <param name="Relation">The name of a <see cref="Schema.RelationKind.Child"/> relation the queried form type declares.</param>
/// <param name="Condition">The condition on the target's rows — a column the target declares; none means every row.</param>
/// <param name="Quantifier">Whether the parent has such a row (the default) or has none.</param>
public sealed record ChildFilter(string Relation, FieldFilter? Condition = null, ChildQuantifier Quantifier = ChildQuantifier.Any)
{
    /// <summary>
    /// Where the relation's rows are and how they join, set by the record query before a store reads the
    /// filter — a caller leaves it unset. A store refuses a child filter that arrives without it.
    /// </summary>
    public ChildJoin? Join { get; init; }
}

/// <summary>How a <see cref="ChildFilter"/> counts the child rows its condition keeps.</summary>
public enum ChildQuantifier
{
    /// <summary>The parent has at least one — OData's <c>any()</c>, SQL's <c>EXISTS</c>.</summary>
    Any,

    /// <summary>The parent has none — OData's <c>not any()</c>, SQL's <c>NOT EXISTS</c>.</summary>
    None,
}

/// <summary>A child relation as a store reads it: a parent row joins the child rows whose key column holds its parent column's value.</summary>
/// <param name="ChildTable">The table of the relation's target.</param>
/// <param name="ChildKeyColumn">The target's column that holds the parent's value.</param>
/// <param name="ParentColumn">The queried table's column whose value the child rows hold — its declared key field, or its record identity.</param>
public sealed record ChildJoin(string ChildTable, string ChildKeyColumn, string ParentColumn);
