using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// The declared field hints for a single form type, plus the physical table name to project into.
/// Absence of a <see cref="FormTypeHints"/> (or an empty field list) means "no projection yet".
/// <paramref name="Relations"/> declares links to other form types (each still its own table —
/// design 2026-07-23 §4), and <paramref name="DeclarationVersion"/> anchors per-row absent-vs-null
/// distinction across schema growth. <paramref name="BusinessName"/> is the name a person reads the
/// form by («설비 점검일지» for <c>inspection_log</c>), as <see cref="FieldHint.BusinessName"/> is for a
/// field — declaration only, never part of the projected table, so changing it rebuilds nothing.
/// <paramref name="Indexes"/> are the indexes the projected table is built with (<see cref="IndexHint"/>) —
/// they change how fast a query is answered, never what it answers. Defaults reproduce the pre-vocabulary shape.
/// </summary>
public sealed record FormTypeHints(
    FormTypeRef Type,
    string TableName,
    IReadOnlyList<FieldHint> Fields,
    IReadOnlyList<RelationHint>? Relations = null,
    int DeclarationVersion = 1,
    string? BusinessName = null,
    IReadOnlyList<IndexHint>? Indexes = null)
{
    /// <summary>
    /// What this declaration says about <paramref name="field"/>'s time binding. A declared binding is what it says. A field
    /// that does not declare one is <see cref="BindingState.Undecided"/> when it takes its value from another form type —
    /// it has a <see cref="FieldHint.Target"/>, or it is the key field of a <see cref="RelationKind.Reference"/> relation —
    /// and <see cref="BindingState.Stored"/> otherwise.
    /// </summary>
    public BindingState BindingStateOf(FieldHint field)
    {
        ArgumentNullException.ThrowIfNull(field);
        return field.Binding switch
        {
            FieldBinding.Stored => BindingState.Stored,
            FieldBinding.Snapshot => BindingState.Snapshot,
            FieldBinding.Reference => BindingState.Reference,
            _ when field.Target is not null
                || (Relations?.Any(r => r.Kind == RelationKind.Reference && string.Equals(r.KeyField, field.Name, StringComparison.Ordinal)) ?? false)
                => BindingState.Undecided,
            _ => BindingState.Stored,
        };
    }

    /// <summary>The name a person reads the form by, when one is declared.</summary>
    /// <exception cref="ArgumentException">The name is given but blank.</exception>
    public string? BusinessName { get; init => field = Checked(value); } = Checked(BusinessName);

    private static string? Checked(string? businessName) =>
        businessName is not null && string.IsNullOrWhiteSpace(businessName)
            ? throw new ArgumentException("A business name, when given, must not be blank.", nameof(businessName))
            : businessName;
}
