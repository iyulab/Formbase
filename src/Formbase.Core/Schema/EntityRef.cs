using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// The declaration-level coordinate a bound field points at: another form type and the field on
/// it. Rendered to a physical <c>table.column</c> string when a proposal materializes.
/// </summary>
public sealed record EntityRef(FormTypeRef Entity, string KeyField);
