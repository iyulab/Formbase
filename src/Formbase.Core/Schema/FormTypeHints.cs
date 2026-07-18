using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// The declared field hints for a single form type, plus the physical table name to project into.
/// Absence of a <see cref="FormTypeHints"/> (or an empty field list) means "no projection yet".
/// </summary>
public sealed record FormTypeHints(FormTypeRef Type, string TableName, IReadOnlyList<FieldHint> Fields);
