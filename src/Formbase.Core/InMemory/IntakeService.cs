using Formbase.Core.Errors;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.InMemory;

/// <summary>
/// Default <see cref="IIntakeService"/>: assigns an id (honoring a supplied idempotency key),
/// appends to the raw store, and wraps any low-level append failure as an <see cref="IntakeException"/>.
/// Store-agnostic — works over any <see cref="IRawStore"/>.
/// </summary>
public sealed class IntakeService : IIntakeService
{
    private readonly IRawStore _rawStore;

    public IntakeService(IRawStore rawStore) => _rawStore = rawStore;

    public Task<DocumentId> AcceptAsync(
        FormTypeRef type,
        DocumentBody body,
        DocumentId? idempotencyId = null,
        RecordKey? recordKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        return AppendAsync(
            type,
            RawAppend.Document(idempotencyId ?? DocumentId.New(), body, recordKey),
            id => _rawStore.AppendAsync(type, id, body, recordKey, cancellationToken),
            $"Failed to accept document for form type '{type}'.");
    }

    public Task<DocumentId> RetireAsync(
        FormTypeRef type,
        RecordKey recordKey,
        DocumentId? idempotencyId = null,
        CancellationToken cancellationToken = default)
        => AppendAsync(
            type,
            RawAppend.Retirement(idempotencyId ?? DocumentId.New(), recordKey),
            id => _rawStore.RetireAsync(type, id, recordKey, cancellationToken),
            $"Failed to retire record '{recordKey}' of form type '{type}'.");

    public async Task<IReadOnlyList<DocumentId>> AcceptManyAsync(
        FormTypeRef type,
        IReadOnlyList<IntakeDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var appends = new RawAppend[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            ArgumentNullException.ThrowIfNull(document, nameof(documents));
            var id = document.IdempotencyId ?? DocumentId.New();
            appends[i] = document.Body is { } body
                ? RawAppend.Document(id, body, document.RecordKey)
                : RawAppend.Retirement(id, (RecordKey)document.RecordKey!); // a retirement always carries its key
        }

        IReadOnlyList<StoredDocument> stored;
        try
        {
            // The store refuses a reused key before anything is written, so a refusal leaves the batch unstored.
            stored = await _rawStore.AppendManyAsync(type, appends, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new IntakeException($"Failed to accept {documents.Count} documents for form type '{type}'.", ex);
        }

        return stored.Select(document => document.Id).ToArray();
    }

    private static async Task<DocumentId> AppendAsync(
        FormTypeRef type,
        RawAppend request,
        Func<DocumentId, Task<StoredDocument>> append,
        string failure)
    {
        StoredDocument stored;
        try
        {
            stored = await append(request.Id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new IntakeException(failure, ex);
        }

        // The store hands back what it already holds for a known id. The same request is a retry;
        // anything else is a second request wearing the first one's key, and accepting it would report a
        // document that was never stored while dropping the one that was sent.
        request.EnsureRepeats(type, stored);
        return stored.Id;
    }
}
