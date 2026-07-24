using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A projection exists for this form type but its integrity is unconfirmed: a rebuild failed and
/// the state cleanup that should have reset it also failed, so the recorded stamp may describe a
/// half-built table. Distinct from <see cref="ProjectionUnavailableException"/> (the store is
/// reachable; it is the recorded state that cannot be trusted) and from
/// <see cref="NotProjectedException"/> (a projection was recorded). The remedy is to re-project.
/// </summary>
public sealed class ProjectionUnverifiedException : FormbaseException
{
    public FormTypeRef FormType { get; }

    public ProjectionUnverifiedException(FormTypeRef formType)
        : base($"Projection for form type '{formType}' is unverified after a failed rebuild; re-project to restore it.")
    {
        FormType = formType;
    }
}
