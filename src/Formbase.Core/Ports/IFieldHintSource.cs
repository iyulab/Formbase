using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Ports;

/// <summary>
/// Supplies the declared field hints for a form type, or null when none are declared. This is the
/// seam an input adapter (M3L, a form builder, manual registration) fills; the core reads hints
/// through it without knowing where they came from.
/// </summary>
public interface IFieldHintSource
{
    Task<FormTypeHints?> GetHintsAsync(FormTypeRef type, CancellationToken cancellationToken = default);

    /// <summary>
    /// The declaration that names <paramref name="section"/> as a repeated section
    /// (<see cref="RelationHint.SectionField"/>), or null when none does — the source a section form
    /// type's rows are projected from.
    /// </summary>
    Task<SectionSource?> GetSectionSourceAsync(FormTypeRef section, CancellationToken cancellationToken = default);
}
