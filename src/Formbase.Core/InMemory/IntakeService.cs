using System.Text.Json;
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
            body,
            recordKey,
            idempotencyId ?? DocumentId.New(),
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
            body: null,
            recordKey,
            idempotencyId ?? DocumentId.New(),
            id => _rawStore.RetireAsync(type, id, recordKey, cancellationToken),
            $"Failed to retire record '{recordKey}' of form type '{type}'.");

    private static async Task<DocumentId> AppendAsync(
        FormTypeRef type,
        DocumentBody? body,
        RecordKey? recordKey,
        DocumentId id,
        Func<DocumentId, Task<StoredDocument>> append,
        string failure)
    {
        StoredDocument stored;
        try
        {
            stored = await append(id).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new IntakeException(failure, ex);
        }

        // The store hands back what it already holds for a known id. The same request is a retry;
        // anything else is a second request wearing the first one's key, and accepting it would report a
        // document that was never stored while dropping the one that was sent. Bodies are compared as
        // JSON values, not text: a durable store gives back its own normalized form (property order,
        // whitespace), which a genuine retry must still match.
        if (!IsSameRequest(stored, type, body, recordKey))
        {
            throw new IdempotencyKeyReusedException(id, type, stored.Type);
        }

        return stored.Id;
    }

    private static bool IsSameRequest(StoredDocument stored, FormTypeRef type, DocumentBody? body, RecordKey? recordKey) =>
        stored.Type == type
        && stored.Key == recordKey
        && (stored.Body, body) switch
        {
            (null, null) => true,
            ({ } held, { } sent) => JsonElement.DeepEquals(held.Root, sent.Root),
            _ => false,
        };
}
