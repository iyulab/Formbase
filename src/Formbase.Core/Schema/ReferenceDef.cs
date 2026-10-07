using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// What a store needs to answer a <see cref="FieldBinding.Reference"/> column at read time: the
/// target's table and the column read from it, how a target row is found, and the column of this
/// table that carries the identifying value. The proposer resolves it from the declared
/// <see cref="EntityRef"/>; a store computes the column from it when the column is read, so the value
/// is the target's current one and the column filters, orders and groups like any other.
/// </summary>
/// <param name="TargetType">The form type the value comes from — its freshness is the column's freshness.</param>
/// <param name="TargetTable">The table <paramref name="TargetType"/> is projected into.</param>
/// <param name="ValueColumn">The column of <paramref name="TargetTable"/> whose value is read.</param>
/// <param name="Lookup">How a target row is found: a column of <paramref name="TargetTable"/>, or the record identity.</param>
/// <param name="ViaColumn">The column of this table whose value identifies the target row.</param>
/// <remarks>
/// When several target rows match, the one read is the latest accepted (the highest
/// <see cref="Projection.ProjectionSystemColumns.Watermark"/>); when none does, the column reads null.
/// </remarks>
public sealed record ReferenceDef(
    FormTypeRef TargetType,
    string TargetTable,
    string ValueColumn,
    TargetLookup Lookup,
    string ViaColumn);
