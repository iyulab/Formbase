using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A projection exists but the backing store (MorphDB) is currently unreachable. Distinct from
/// <see cref="NotProjectedException"/>: the projection is fine, the query path is temporarily down.
/// </summary>
public sealed class ProjectionUnavailableException : FormbaseException
{
    public FormTypeRef FormType { get; }

    public ProjectionUnavailableException(FormTypeRef formType, Exception? innerException = null)
        : base($"Projection for form type '{formType}' is temporarily unavailable.", innerException)
    {
        FormType = formType;
    }
}
