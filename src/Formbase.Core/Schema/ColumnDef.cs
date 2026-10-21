namespace Formbase.Core.Schema;

/// <summary>
/// A single column in a projected table schema. Beyond name/type/nullability it carries the
/// declaration axes that must survive into the projection: <paramref name="SourceKey"/> (the raw
/// extraction key when it differs from the projected <paramref name="Name"/>) and the time
/// binding (<paramref name="Binding"/> with an optional <paramref name="BindingTarget"/> rendered
/// as <c>table.column</c>). A reference whose declaration says how to find its target record also
/// carries <paramref name="Reference"/>, from which a store computes it at read time. A computed field carries
/// its <paramref name="Derivation"/>, applied when the projection reads the document. Defaults
/// reproduce the pre-vocabulary behavior exactly.
/// </summary>
public sealed record ColumnDef(
    string Name,
    ColumnType Type,
    bool Nullable = true,
    string? SourceKey = null,
    FieldBinding Binding = FieldBinding.Stored,
    string? BindingTarget = null,
    ReferenceDef? Reference = null,
    FieldDerivation? Derivation = null)
{
    /// <summary>
    /// Where in a raw document this column reads from, when it differs from <see cref="Name"/> — a top-level
    /// member name, or a JSON Pointer (<see cref="SourceKeys"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The key is given but blank, or not a valid JSON Pointer.</exception>
    public string? SourceKey { get; init => field = SourceKeys.Checked(value, nameof(SourceKey)); } = SourceKeys.Checked(SourceKey, nameof(SourceKey));

    /// <summary>
    /// The key this column reads a raw document by (<see cref="SourceKeys"/>) — <see cref="SourceKey"/> when set,
    /// else the top-level member named <see cref="Name"/>.
    /// </summary>
    public string ExtractionKey => SourceKey ?? SourceKeys.ForMember(Name);
}
