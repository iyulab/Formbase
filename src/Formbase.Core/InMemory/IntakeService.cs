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
/// <para>
/// A document is stamped with the declaration in force when intake looked
/// (<see cref="StoredDocument.DeclarationSeq"/>) — <c>0</c> when none was — if the declarations keep a history
/// (<see cref="IFieldHintHistory"/>); over a source that does not, the stamp is left unknown. The declaration
/// and the raw store are two stores, so a declaration written between the look and the append is not seen: the
/// stamp names what intake saw, which is also what a caller that read the declaration just before posting saw.
/// </para>
/// </summary>
public sealed class IntakeService : IIntakeService
{
    private readonly IRawStore _rawStore;
    private readonly IFieldHintSource _hints;
    private readonly IFieldHintHistory? _history;

    public IntakeService(IRawStore rawStore, IFieldHintSource hints)
    {
        _rawStore = rawStore;
        _hints = hints;
        _history = hints as IFieldHintHistory;
    }

    public async Task<DocumentId> AcceptAsync(
        FormTypeRef type,
        DocumentBody body,
        DocumentId? idempotencyId = null,
        RecordKey? recordKey = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(body);
        EnsureStorable(body, recordKey);
        await EnsureNotASectionAsync(type, cancellationToken).ConfigureAwait(false);
        var declarationSeq = await CurrentDeclarationAsync(type, cancellationToken).ConfigureAwait(false);
        var request = RawAppend.Document(idempotencyId ?? DocumentId.New(), body, recordKey, declarationSeq);
        return await AppendAsync(
            type,
            request,
            () => _rawStore.AppendAsync(type, request, cancellationToken),
            $"Failed to accept document for form type '{type}'.").ConfigureAwait(false);
    }

    public async Task<DocumentId> RetireAsync(
        FormTypeRef type,
        RecordKey recordKey,
        DocumentId? idempotencyId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureStorable(null, recordKey);
        await EnsureNotASectionAsync(type, cancellationToken).ConfigureAwait(false);
        var request = RawAppend.Retirement(idempotencyId ?? DocumentId.New(), recordKey);
        return await AppendAsync(
            type,
            request,
            () => _rawStore.AppendAsync(type, request, cancellationToken),
            $"Failed to retire record '{recordKey}' of form type '{type}'.").ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DocumentId>> AcceptManyAsync(
        FormTypeRef type,
        IReadOnlyList<IntakeDocument> documents,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(documents);
        await EnsureNotASectionAsync(type, cancellationToken).ConfigureAwait(false);
        var declarationSeq = await CurrentDeclarationAsync(type, cancellationToken).ConfigureAwait(false);

        var appends = new RawAppend[documents.Count];
        for (var i = 0; i < documents.Count; i++)
        {
            var document = documents[i];
            ArgumentNullException.ThrowIfNull(document, nameof(documents));
            EnsureStorable(document.Body, document.RecordKey);
            var id = document.IdempotencyId ?? DocumentId.New();
            appends[i] = document.Body is { } body
                ? RawAppend.Document(id, body, document.RecordKey, declarationSeq)
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
    /// Refuses what a durable store may not be able to hold, before any store is asked — the same on every store
    /// (<see cref="UnstorableDocumentException"/>). A batch is checked in full before the first append, so a refused
    /// batch stores nothing.
    /// </summary>
    private static void EnsureStorable(DocumentBody? body, RecordKey? recordKey)
    {
        if (body?.FindNulCharacter() is { } at)
        {
            throw new UnstorableDocumentException(at);
        }

        if (recordKey is { Value: var key } && key.Contains('\0', StringComparison.Ordinal))
        {
            throw new UnstorableDocumentException("the record key");
        }
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

    /// <summary>
    /// The declaration documents are accepted under now: its sequence, <c>0</c> when none is in force, or
    /// <see langword="null"/> (unknown) when the declarations keep no history. Read once per intake call, so a
    /// batch is stamped alike. A history that cannot be read fails the intake as an unreadable declaration does.
    /// </summary>
    private async Task<int?> CurrentDeclarationAsync(FormTypeRef type, CancellationToken cancellationToken)
    {
        if (_history is null)
        {
            return null;
        }

        try
        {
            return await _history.GetCurrentSequenceAsync(type, cancellationToken).ConfigureAwait(false) ?? 0;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new IntakeException($"Failed to accept a document for form type '{type}': its declaration history could not be read.", ex);
        }
    }

    private static async Task<DocumentId> AppendAsync(
        FormTypeRef type,
        RawAppend request,
        Func<Task<StoredDocument>> append,
        string failure)
    {
        StoredDocument stored;
        try
        {
            stored = await append().ConfigureAwait(false);
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
