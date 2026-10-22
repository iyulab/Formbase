namespace Formbase.Core.Query;

/// <summary>
/// A condition on a child relation: the parent rows that have at least one row of the relation's target
/// the condition keeps — «the inspections with a dent checked», «the orders with a line over 100». Like
/// OData's <c>any()</c>, it asks about existence, so a parent with several matching rows is still one row.
/// A query's child filters all apply, each about its own relation, alongside its field filters.
/// </summary>
/// <param name="Relation">The name of a <see cref="Schema.RelationKind.Child"/> relation the queried form type declares.</param>
/// <param name="Condition">The condition on the target's rows — a column the target declares.</param>
public sealed record ChildFilter(string Relation, FieldFilter Condition)
{
    /// <summary>
    /// Where the relation's rows are and how they join, set by the record query before a store reads the
    /// filter — a caller leaves it unset. A store refuses a child filter that arrives without it.
    /// </summary>
    public ChildJoin? Join { get; init; }
}

/// <summary>A child relation as a store reads it: a parent row joins the child rows whose key column holds its parent column's value.</summary>
/// <param name="ChildTable">The table of the relation's target.</param>
/// <param name="ChildKeyColumn">The target's column that holds the parent's value.</param>
/// <param name="ParentColumn">The queried table's column whose value the child rows hold — its declared key field, or its record identity.</param>
public sealed record ChildJoin(string ChildTable, string ChildKeyColumn, string ParentColumn);
