namespace Formbase.Core.Errors;

/// <summary>
/// The raw store failed to durably append a document. This is the only failure surfaced to the
/// submitting adapter — recovery is re-submission (safe via the idempotency key).
/// </summary>
public sealed class IntakeException : FormbaseException
{
    public IntakeException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
