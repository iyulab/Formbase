using Formbase.Core.Query;

namespace Formbase.Host.Contracts;

/// <summary>
/// Records read from a form type's projection.
/// </summary>
/// <param name="Rows">
/// The projected records, each as which record it is and its declared fields.
/// </param>
/// <param name="Stale">
/// True when raw documents arrived after the projection was built, so these rows are current as of
/// an earlier point in the stream. The rows are still served: a stale answer a caller knows is
/// stale is more useful than a refusal.
/// </param>
public sealed record RecordQueryResponse(
    IReadOnlyList<RecordRowResponse> Rows,
    bool Stale);

/// <summary>
/// One projected record.
/// </summary>
/// <param name="Record">Which record this is.</param>
/// <param name="Fields">
/// Exactly the declared columns — the projection's own bookkeeping is not part of what a caller
/// reads, and an internal that leaks here would calcify into their contract.
/// </param>
public sealed record RecordRowResponse(
    RecordRefResponse Record,
    IReadOnlyDictionary<string, object?> Fields)
{
    internal static RecordRowResponse From(RecordRow row) =>
        new(new RecordRefResponse(row.Record.Document.Value, row.Record.Key?.Value), row.Fields);
}

/// <summary>
/// Which record a row is: the document standing for it now, and the key that names the record across
/// corrections when it has one.
/// </summary>
/// <param name="Document">
/// The document the record's values come from now — the one to read back for its full body. A
/// correction moves a keyed record to a new document, so this is not what to remember a record by.
/// </param>
/// <param name="Key">
/// The record's key, the same through every correction — or null for a document sent without one,
/// which is a record of its own and whose document is its identity.
/// </param>
public sealed record RecordRefResponse(Guid Document, string? Key);
