namespace Formbase.Core.Errors;

/// <summary>
/// The projection store refused a row of a document: a value it does not accept for the column (a null for a
/// required one, a value of another type). The store is reachable and answered, so this is not an outage
/// (<see cref="ProjectionUnavailableException"/>, which passes when the store comes back): retrying changes nothing. The
/// document the row came from, or the declaration it is projected under, is what to change.
/// </summary>
public sealed class ProjectionRowRefusedException : FormbaseException
{
    /// <summary>The document the refused row was cut from, as the row names it.</summary>
    public string? Document { get; }

    /// <summary>The record key the refused row's document was filed under, or null when it names none.</summary>
    public string? RecordKey { get; }

    /// <summary>The projection table the row was meant for.</summary>
    public string Table { get; }

    /// <summary>What the store said about the row, in its own words.</summary>
    public string Reason { get; }

    public ProjectionRowRefusedException(string? document, string? recordKey, string table, string reason, Exception? innerException = null)
        : base(
            $"The projection store refused the row of document '{document}'{(recordKey is null ? string.Empty : $" (record key '{recordKey}')")} for '{table}': {reason} " +
            "The store answered, so retrying changes nothing: correct that document, or the declaration it is projected under, and project again.",
            innerException)
    {
        Document = document;
        RecordKey = recordKey;
        Table = table;
        Reason = reason;
    }
}
