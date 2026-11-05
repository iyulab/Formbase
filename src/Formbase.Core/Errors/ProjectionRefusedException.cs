namespace Formbase.Core.Errors;

/// <summary>
/// The projection store refused the table a declaration describes: a table or field name it does not
/// accept (a reserved name, one that is too long, one that starts with an underscore, a name the host adds
/// to every row itself). The store is reachable and healthy — it answered — so this is not an outage
/// (<see cref="ProjectionUnavailableException"/>, which passes when the store comes back): nothing is
/// retried into success. The declaration is what to change, then project again.
/// </summary>
public sealed class ProjectionRefusedException : FormbaseException
{
    /// <summary>What the store said about the definition it refused, in its own words.</summary>
    public string Reason { get; }

    public ProjectionRefusedException(string reason, Exception? innerException = null)
        : base(
            $"The projection store refused the table this declaration describes: {reason} " +
            "Change the declaration (a table or field name the store does not accept) and project again.",
            innerException)
    {
        Reason = reason;
    }
}
