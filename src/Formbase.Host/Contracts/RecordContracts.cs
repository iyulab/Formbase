using System.Text.Json.Serialization;
using Formbase.Core.Primitives;
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
/// <param name="SkippedDocuments">
/// How many accepted documents the form type's projection could not project — they are in none of the
/// rows, so the answer is not over every document. Zero when every document stands. `GET /formtypes/{type}/projection/skips` names them and why.
/// </param>
public sealed record RecordQueryResponse(
    IReadOnlyList<RecordRowResponse> Rows,
    bool Stale,
    int SkippedDocuments);

/// <summary>
/// One projected record.
/// </summary>
/// <param name="Record">Which record this is.</param>
/// <param name="Fields">
/// Exactly the declared columns — the projection's own bookkeeping is not part of what a caller
/// reads, and an internal that leaks here would calcify into their contract.
/// </param>
/// <param name="References">
/// Where each reference field's value was read from, by field name — only when the query asked for it
/// with <c>referenceSources=true</c>; absent otherwise.
/// </param>
public sealed record RecordRowResponse(
    RecordRefResponse Record,
    IReadOnlyDictionary<string, object?> Fields,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyDictionary<string, ReferenceSourceResponse>? References = null)
{
    internal static RecordRowResponse From(RecordRow row) =>
        new(
            RecordRefResponse.From(row.Record),
            row.Fields,
            row.References?.ToDictionary(
                pair => pair.Key,
                pair => new ReferenceSourceResponse(
                    pair.Value.Target is { } target ? RecordRefResponse.From(target) : null,
                    pair.Value.Outcome),
                StringComparer.Ordinal));
}

/// <summary>
/// Where a reference field's value was read from, in the same read as the value.
/// </summary>
/// <param name="Target">
/// The target record the value is that record's field of, as it is now — null unless
/// <paramref name="Outcome"/> is <c>resolved</c>.
/// </param>
/// <param name="Outcome">
/// <c>resolved</c>: a target record was found (its field may still be empty) · <c>noTarget</c>: the via
/// field is empty or matches no target record — a retired one included · <c>targetUnavailable</c>: the
/// target's projection does not exist, or is not yet rebuilt into the shape the reference reads.
/// </param>
public sealed record ReferenceSourceResponse(RecordRefResponse? Target, ReferenceOutcome Outcome);

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
/// <param name="Row">
/// For a row of a repeated section, its position in the section of <paramref name="Document"/>, from 0 —
/// the rows one document holds share its document and key. Absent for a record of its own.
/// </param>
public sealed record RecordRefResponse(
    Guid Document,
    string? Key,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Row = null)
{
    internal static RecordRefResponse From(RecordRef record) => new(record.Document.Value, record.Key?.Value, record.Row);
}
