using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// A declared field for a form type — the input to hint-based schema proposal, and the unit an
/// input adapter (M3L, a form builder, manual registration) produces. Beyond the minimal
/// name/type/nullability it carries the measured declaration axes (design 2026-07-23, adopted
/// §3.12-①): <paramref name="SourceKey"/> splits the raw extraction key (identity) from the
/// projected <paramref name="Name"/> (display), and <paramref name="Binding"/> +
/// <paramref name="Target"/> record the time-binding of a bound value. <paramref name="BusinessName"/>
/// is the name a person reads the field by («로트 번호» for <c>lot_no</c>) — ODCS v3.1's
/// <c>businessName</c> beside the machine names. It is declaration only: it never reaches the projected
/// table, so changing it rebuilds nothing. Defaults reproduce the pre-vocabulary behavior: a stored
/// field whose name is its own extraction key.
/// </summary>
public sealed record FieldHint(
    string Name,
    ColumnType Type,
    bool Nullable = true,
    string? SourceKey = null,
    FieldBinding Binding = FieldBinding.Stored,
    EntityRef? Target = null,
    string? BusinessName = null)
{
    /// <summary>
    /// Where in a document the field is read from, when it differs from <see cref="Name"/> — a top-level
    /// member name, or a JSON Pointer such as <c>/commit/collection</c> (<see cref="SourceKeys"/>).
    /// </summary>
    /// <exception cref="ArgumentException">The key is given but blank, or not a valid JSON Pointer.</exception>
    public string? SourceKey { get; init => field = SourceKeys.Checked(value, nameof(SourceKey)); } = SourceKeys.Checked(SourceKey, nameof(SourceKey));

    /// <summary>The name a person reads the field by, when one is declared.</summary>
    /// <exception cref="ArgumentException">The name is given but blank.</exception>
    public string? BusinessName { get; init => field = Checked(value); } = Checked(BusinessName);

    private static string? Checked(string? businessName) =>
        businessName is not null && string.IsNullOrWhiteSpace(businessName)
            ? throw new ArgumentException("A business name, when given, must not be blank.", nameof(businessName))
            : businessName;
}
