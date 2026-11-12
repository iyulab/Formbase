namespace Formbase.Core.Errors;

/// <summary>
/// A document or record key carries something a durable store may not be able to hold — the character
/// U+0000, which PostgreSQL's <c>jsonb</c> and <c>text</c> refuse for good. It is refused at intake, before
/// any store is asked and the same on every store: left to the store it would be refused by one and kept by
/// another, and the refusal would come back wrapped as an intake failure a caller reads as «try again».
/// Nothing was stored.
/// </summary>
public sealed class UnstorableDocumentException : FormbaseException
{
    /// <summary>Where the character is: a JSON path in the body, or the record key.</summary>
    public string Location { get; }

    public UnstorableDocumentException(string location)
        : base($"The document cannot be stored: {location} contains the character U+0000, which a durable store may not be able to hold. Remove it and send the document again.")
    {
        Location = location;
    }
}
