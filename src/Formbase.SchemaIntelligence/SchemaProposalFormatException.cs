using Formbase.Core.Errors;

namespace Formbase.SchemaIntelligence;

/// <summary>
/// The LLM's schema proposal could not be accepted: not valid JSON, not the requested JSON Schema
/// shape, an unsupported type, or a property that never appears in the sampled documents. Thrown
/// instead of silently repairing — a proposer that guesses past a malformed proposal would project
/// a shape nobody vouched for.
/// </summary>
public sealed class SchemaProposalFormatException : FormbaseException
{
    public SchemaProposalFormatException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
