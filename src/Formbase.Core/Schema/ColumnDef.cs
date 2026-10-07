namespace Formbase.Core.Schema;

/// <summary>
/// A single column in a projected table schema. Beyond name/type/nullability it carries the
/// declaration axes that must survive into the projection: <paramref name="SourceKey"/> (the raw
/// extraction key when it differs from the projected <paramref name="Name"/>) and the time
/// binding (<paramref name="Binding"/> with an optional <paramref name="BindingTarget"/> rendered
/// as <c>table.column</c>). A reference whose declaration says how to find its target record also
/// carries <paramref name="Reference"/>, from which a store computes it at read time. Defaults
/// reproduce the pre-vocabulary behavior exactly.
/// </summary>
public sealed record ColumnDef(
    string Name,
    ColumnType Type,
    bool Nullable = true,
    string? SourceKey = null,
    FieldBinding Binding = FieldBinding.Stored,
    string? BindingTarget = null,
    ReferenceDef? Reference = null)
{
    /// <summary>The raw document key this column reads from — <see cref="SourceKey"/> when set, else <see cref="Name"/>.</summary>
    public string ExtractionKey => SourceKey ?? Name;
}
