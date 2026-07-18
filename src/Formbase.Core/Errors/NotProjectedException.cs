using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A record query was issued against a form type that has no projection yet. Distinct from an
/// empty result: the consumer's remedy is to declare field hints or trigger a projection.
/// </summary>
public sealed class NotProjectedException : FormbaseException
{
    public FormTypeRef FormType { get; }

    public NotProjectedException(FormTypeRef formType)
        : base($"Form type '{formType}' has no projection; declare field hints or trigger a projection.")
    {
        FormType = formType;
    }
}
