using System.Text.Json;

namespace Formbase.Mcp;

/// <summary>
/// What the MCP tools may ask of the host, and all they may ask: the host implements it over the read paths its REST API
/// already uses, for the namespace the request was scoped to. It has no member that writes, so a tool cannot either.
/// Answers travel as JSON in the REST API's own shape, so a reader of either door reads the same words.
/// </summary>
public interface IFormbaseReadPort
{
    /// <summary>The declaration a form type has in force, or <see langword="null"/> when it has none.</summary>
    ValueTask<JsonElement?> DescribeFormTypeAsync(string type, CancellationToken cancellationToken);

    /// <summary>One read of a form type's projected records.</summary>
    /// <exception cref="FormbaseReadRefusedException">The type is unknown or not projected, or a filter or ordering is not accepted.</exception>
    ValueTask<JsonElement> QueryRecordsAsync(FormbaseRecordQuery query, CancellationToken cancellationToken);

    /// <summary>Whether a form type's projection exists and can be trusted.</summary>
    /// <exception cref="FormbaseReadRefusedException">The type is unknown.</exception>
    ValueTask<JsonElement> ProjectionStatusAsync(string type, CancellationToken cancellationToken);

    /// <summary>One page of a form type's raw stream, oldest first, whether or not the type has a declaration or a projection.</summary>
    /// <exception cref="FormbaseReadRefusedException">The form type's name cannot be one, or the watermark is negative.</exception>
    ValueTask<JsonElement> ListDocumentsAsync(FormbaseDocumentQuery query, CancellationToken cancellationToken);

    /// <summary>One stored document, or <see langword="null"/> when there is none with this id.</summary>
    /// <exception cref="FormbaseReadRefusedException">The id is not a document id.</exception>
    ValueTask<JsonElement?> GetDocumentAsync(string id, CancellationToken cancellationToken);
}

/// <summary>The read cannot be made as asked; the message says why, in words the caller can act on.</summary>
public sealed class FormbaseReadRefusedException : Exception
{
    /// <summary>Creates the refusal.</summary>
    public FormbaseReadRefusedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the refusal, with the exception it follows from.</summary>
    public FormbaseReadRefusedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A read of a form type's records, by the record endpoint's rules.</summary>
/// <param name="Type">The form type.</param>
/// <param name="Filter">Equality conditions, one <c>column:value</c> each; all apply.</param>
/// <param name="OrderBy">Columns to order by, comma-separated, a leading <c>-</c> reversing one.</param>
/// <param name="Limit">The most rows to return; the door caps it.</param>
/// <param name="Offset">Rows to skip first.</param>
/// <param name="ReferenceSources">Whether to say, for each reference field, the record its value was read from.</param>
public sealed record FormbaseRecordQuery(string Type, IReadOnlyList<string>? Filter, string? OrderBy, int Limit, int Offset, bool ReferenceSources);

/// <summary>One read of records: the host's answer, and the window it was asked for.</summary>
/// <param name="Limit">The row limit applied.</param>
/// <param name="Offset">The rows skipped.</param>
/// <param name="Result">The host's answer: <c>rows</c>, whether the projection is <c>stale</c>, and how many documents it <c>skippedDocuments</c>.</param>
public sealed record FormbaseRecordPage(int Limit, int Offset, JsonElement Result);

/// <summary>A read of a page of a form type's raw stream, by the document endpoint's rules.</summary>
/// <param name="Type">The form type.</param>
/// <param name="After">The watermark to read after; <c>0</c> reads from the start of the stream.</param>
/// <param name="Limit">The most documents to return; the door caps it.</param>
public sealed record FormbaseDocumentQuery(string Type, long After, int Limit);

/// <summary>One read of the raw stream: the host's answer, and the window it was asked for.</summary>
/// <param name="Limit">The document limit applied.</param>
/// <param name="After">The watermark the page follows.</param>
/// <param name="Result">The host's answer: the <c>documents</c> oldest first and the stream's <c>rawHead</c> — when the last document's watermark equals it, everything has been read.</param>
public sealed record FormbaseDocumentPage(int Limit, long After, JsonElement Result);
