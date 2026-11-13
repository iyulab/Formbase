using System.Text.Json;

namespace Formbase.Core.Primitives;

/// <summary>
/// One append of a batch handed to <see cref="Ports.IRawStore.AppendManyAsync"/>: a document, or a
/// retirement of a record, under an id that is also its idempotency key. The form type is the batch's.
/// </summary>
public sealed class RawAppend
{
    /// <summary>The id the append is stored under — adapter-supplied, so a retried batch stores nothing twice.</summary>
    public DocumentId Id { get; }

    /// <summary>The content, or <see langword="null"/> for a retirement (see <see cref="StoredDocument.Body"/>).</summary>
    public DocumentBody? Body { get; }

    /// <summary>The record the append belongs to. Always present on a retirement.</summary>
    public RecordKey? Key { get; }

    /// <summary>
    /// The declaration the document is accepted under (see <see cref="StoredDocument.DeclarationSeq"/>), or
    /// <see langword="null"/> when the caller does not say. It is a stamp, not part of the request: a retry
    /// of the same append that finds a declaration written in between is still the same append, and gets the
    /// document it first stored — with the stamp it first had.
    /// </summary>
    public int? DeclarationSeq { get; }

    private RawAppend(DocumentId id, DocumentBody? body, RecordKey? key, int? declarationSeq)
    {
        Id = id;
        Body = body;
        Key = key;
        DeclarationSeq = declarationSeq;
    }

    /// <summary>Whether this append retires the record named by <see cref="Key"/> rather than carrying content.</summary>
    public bool IsRetirement => Body is null;

    /// <summary>A document, as <see cref="Ports.IRawStore.AppendAsync(FormTypeRef, DocumentId, DocumentBody, RecordKey?, CancellationToken)"/> appends it.</summary>
    /// <param name="id">The id the document is stored under.</param>
    /// <param name="body">The content.</param>
    /// <param name="key">The record the document belongs to, if it corrects one.</param>
    /// <param name="declarationSeq">The declaration it is accepted under (<see cref="StoredDocument.DeclarationSeq"/>), when the caller knows.</param>
    public static RawAppend Document(DocumentId id, DocumentBody body, RecordKey? key = null, int? declarationSeq = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfNegative(declarationSeq ?? 0, nameof(declarationSeq));
        return new RawAppend(id, body, key, declarationSeq);
    }

    /// <summary>A retirement of the record <paramref name="key"/>, as <see cref="Ports.IRawStore.RetireAsync"/> appends it.</summary>
    public static RawAppend Retirement(DocumentId id, RecordKey key) => new(id, body: null, key, declarationSeq: null);

    /// <summary>
    /// Whether <paramref name="held"/> is what this append, under <paramref name="type"/>, stores — the
    /// same form type, record key and retirement, and a body equal as a JSON value. Bodies are compared as
    /// values, not text: a durable store gives back its own normalized form (property order, whitespace),
    /// which a genuine retry must still match. An id held by anything else is a key reused for another
    /// request, not a retry.
    /// </summary>
    public bool Repeats(FormTypeRef type, StoredDocument held)
    {
        ArgumentNullException.ThrowIfNull(held);
        return held.Type == type && Carries(held.Key, held.Body);
    }

    /// <summary>
    /// Checks a batch before anything in it is written, and returns its appends with each id once, in
    /// the order the ids first appear. An id repeated with the same request is one append; an id repeated
    /// with another request refuses the batch.
    /// </summary>
    /// <exception cref="Errors.IdempotencyKeyReusedException">An id appears twice with different requests.</exception>
    public static IReadOnlyList<RawAppend> Distinct(FormTypeRef type, IReadOnlyList<RawAppend> appends)
    {
        ArgumentNullException.ThrowIfNull(appends);

        var seen = new Dictionary<DocumentId, RawAppend>();
        var distinct = new List<RawAppend>(appends.Count);
        foreach (var append in appends)
        {
            ArgumentNullException.ThrowIfNull(append, nameof(appends));
            if (seen.TryGetValue(append.Id, out var first))
            {
                if (!first.Carries(append.Key, append.Body))
                {
                    throw new Errors.IdempotencyKeyReusedException(append.Id, type, type);
                }

                continue;
            }

            seen.Add(append.Id, append);
            distinct.Add(append);
        }

        return distinct;
    }

    /// <summary>
    /// Refuses the batch if <paramref name="held"/>, the document a store already keeps under this
    /// append's id, is not what this append stores.
    /// </summary>
    /// <exception cref="Errors.IdempotencyKeyReusedException"><paramref name="held"/> is another request.</exception>
    public void EnsureRepeats(FormTypeRef type, StoredDocument held)
    {
        if (!Repeats(type, held))
        {
            throw new Errors.IdempotencyKeyReusedException(Id, type, held.Type);
        }
    }

    private bool Carries(RecordKey? key, DocumentBody? body) =>
        Key == key
        && (Body, body) switch
        {
            (null, null) => true,
            ({ } mine, { } theirs) => JsonElement.DeepEquals(mine.Root, theirs.Root),
            _ => false,
        };
}
