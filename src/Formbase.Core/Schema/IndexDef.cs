namespace Formbase.Core.Schema;

/// <summary>
/// The physical rendering of a declared <see cref="IndexHint"/>, as delivered to the projection store:
/// the columns, in index order. Each store builds it with the table in its own way, or — a store with
/// nothing to index, such as the in-memory one — keeps nothing; what a query answers is the same either way.
/// </summary>
public sealed record IndexDef(IReadOnlyList<string> Columns);
