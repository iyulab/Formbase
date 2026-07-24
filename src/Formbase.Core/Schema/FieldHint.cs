using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// A declared field for a form type — the input to hint-based schema proposal, and the unit an
/// input adapter (M3L, a form builder, manual registration) produces. Beyond the minimal
/// name/type/nullability it carries the measured declaration axes (design 2026-07-23, adopted
/// §3.12-①): <paramref name="SourceKey"/> splits the raw extraction key (identity) from the
/// projected <paramref name="Name"/> (display), and <paramref name="Binding"/> +
/// <paramref name="Target"/> record the time-binding of a bound value. Defaults reproduce the
/// pre-vocabulary behavior: a stored field whose name is its own extraction key.
/// </summary>
public sealed record FieldHint(
    string Name,
    ColumnType Type,
    bool Nullable = true,
    string? SourceKey = null,
    FieldBinding Binding = FieldBinding.Stored,
    EntityRef? Target = null);
