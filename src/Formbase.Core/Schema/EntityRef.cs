using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// The declaration-level coordinate a bound field points at: another form type and the field on
/// it. Rendered to a physical <c>table.column</c> string when a proposal materializes.
/// </summary>
/// <param name="Entity">The form type the bound value comes from.</param>
/// <param name="KeyField">
/// The field on <paramref name="Entity"/> whose value the bound field carries — the column a
/// <c>snapshot</c> was copied from, or the column a <c>reference</c> reads. It is not a lookup key:
/// the coordinate does not say which record of <paramref name="Entity"/> the value belongs to, nor
/// which field of the local document carries that record's key.
/// </param>
public sealed record EntityRef(FormTypeRef Entity, string KeyField);
