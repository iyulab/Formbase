namespace Formbase.Core.Schema;

/// <summary>
/// A single column in a projected table schema. Beyond name/type/nullability it carries the
/// declaration axes that must survive into the projection: <paramref name="SourceKey"/> (the raw
/// extraction key when it differs from the projected <paramref name="Name"/>) and the time
/// binding (<paramref name="Binding"/> with an optional <paramref name="BindingTarget"/> rendered
/// as <c>table.column</c>). Defaults reproduce the pre-vocabulary behavior exactly.
/// </summary>
public sealed record ColumnDef(
    string Name,
    ColumnType Type,
    bool Nullable = true,
    string? SourceKey = null,
    FieldBinding Binding = FieldBinding.Stored,
    string? BindingTarget = null)
{
    /// <summary>The raw document key this column reads from — <see cref="SourceKey"/> when set, else <see cref="Name"/>.</summary>
    public string ExtractionKey => SourceKey ?? Name;
}
