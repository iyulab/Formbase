using Formbase.Core.Primitives;

namespace Formbase.Core.Errors;

/// <summary>
/// A schema proposer's backing system (e.g. an LLM endpoint) could not be reached or did not
/// respond. Distinct from <see cref="ProjectionUnavailableException"/>: the projection store is
/// fine, the proposer itself couldn't answer.
/// </summary>
public sealed class SchemaProposerUnavailableException : FormbaseException
{
    public FormTypeRef FormType { get; }

    public SchemaProposerUnavailableException(FormTypeRef formType, Exception? innerException = null)
        : base($"The schema proposer for form type '{formType}' could not be reached.", innerException)
    {
        FormType = formType;
    }
}
