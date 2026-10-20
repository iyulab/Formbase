namespace Formbase.Core.Errors;

/// <summary>
/// The place the projection store writes to no longer exists — for MorphDB, the project the host was
/// configured with was deleted after the host started. Distinct from
/// <see cref="ProjectionUnavailableException"/>: that one passes when the store comes back, this one
/// does not pass by retrying — the store has to be given a location again (and the projections
/// rebuilt into it). The raw stream is untouched; documents are still accepted.
/// </summary>
public sealed class ProjectionStoreMissingException : FormbaseException
{
    public ProjectionStoreMissingException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }
}
