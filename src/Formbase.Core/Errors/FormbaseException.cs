namespace Formbase.Core.Errors;

/// <summary>Base type for all formbase engine errors.</summary>
public abstract class FormbaseException : Exception
{
    protected FormbaseException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
