using Formbase.Core.Errors;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.InMemory;

/// <summary>
/// Default <see cref="IIntakeService"/>: assigns an id (honoring a supplied idempotency key),
/// appends to the raw store, and wraps any low-level append failure as an <see cref="IntakeException"/>.
/// Store-agnostic — works over any <see cref="IRawStore"/>. No declaration is needed to accept a document;
/// the one question asked of the declarations is whether the form type is a repeated section of another
/// (<see cref="FormTypeIsASectionException"/>), which accepts no documents of its own.
/// </summary>
public sealed class IntakeService : IIntakeService
{
    private readonly IRawStore _rawStore;
    private readonly IFieldHintSource _hints;

    public IntakeService(IRawStore rawStore, IFieldHintSource hints)
    {
        _rawStore = rawStore;
        _hints = hints;
    }

    public async Task<DocumentId> AcceptAsync(
        FormTypeRef type,
        DocumentBody body,
        DocumentId? idempotencyId = null,
        RecordKey? recordKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        await EnsureNotASectionAsync(type, cancellationToken).ConfigureAwait(false);
        return await AppendAsync(
            type,
            RawAppend.Document(idempotencyId ?? DocumentId.New(), body, recordKey),
            id => _rawStore.AppendAsync(type, id, body, recordKey, cancellationToken),
            $"Failed to accept document for form type '{type}'.").ConfigureAwait(false);
    }

    public async Task<DocumentId> RetireAsync(
        FormTypeRef type,
        RecordKey recordKey,
        DocumentId? idempotencyId = null,
        CancellationToken cancellationToken = default)
    {
        await EnsureNotASectionAsync(type, cancellationToken).ConfigureAwait(false);
        return await AppendAsync(
            type,
            RawAppend.Retirement(idempotencyId ?? DocumentId.New(), recordKey),
            id => _rawStore.RetireAsync(type, id, recordKey, cancellationToken),
            $"Failed to retire record '{recordKey}' of form type '{type}'.").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DocumentId>> AcceptManyAsync(
        FormTypeRef type,
        IReadOnlyList<IntakeDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        await EnsureNotASectionAsync(type, cancellationToken).ConfigureAwait(false);

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

    /// <summary>
    /// Refuses a document under a repeated section's form type. Asked before anything is written, so a
    /// refused batch stores nothing. A declaration store that cannot be read fails the intake the way a raw
    /// store would: the document was not accepted, and saying so is the honest answer.
    /// </summary>
    private async Task EnsureNotASectionAsync(FormTypeRef type, CancellationToken cancellationToken)
    {
        SectionSource? section;
        try
        {
            section = await _hints.GetSectionSourceAsync(type, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new IntakeException($"Failed to accept a document for form type '{type}': its declarations could not be read.", ex);
        }

        if (section is not null)
        {
            throw new FormTypeIsASectionException(type, section.Parent.Type, section.SectionField);
        }
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
