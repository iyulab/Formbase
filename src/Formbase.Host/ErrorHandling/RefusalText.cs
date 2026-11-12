namespace Formbase.Host.ErrorHandling;

/// <summary>What a refusal says, as the caller reads it.</summary>
internal static class RefusalText
{
    /// <summary>
    /// The message of an argument exception without the note the framework appends to it, <c>(Parameter 'hints')</c>: the name
    /// of a parameter in this code is not something the caller of an HTTP route has to fix, and showing it reads as a leak of the
    /// implementation. The wording the engine wrote stays whole.
    /// </summary>
    public static string Of(ArgumentException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var message = exception.Message;
        if (exception.ParamName is { Length: > 0 } name)
        {
            var note = $" (Parameter '{name}')";
            if (message.EndsWith(note, StringComparison.Ordinal))
            {
                return message[..^note.Length];
            }
        }

        return message;
    }
}
