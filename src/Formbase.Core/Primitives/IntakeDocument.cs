namespace Formbase.Core.Primitives;

/// <summary>
/// One document of a batch handed to <see cref="Ports.IIntakeService.AcceptManyAsync"/> — what
/// <see cref="Ports.IIntakeService.AcceptAsync"/> or <see cref="Ports.IIntakeService.RetireAsync"/> takes
/// for a single one. The form type is the batch's.
/// </summary>
public sealed class IntakeDocument
{
    /// <summary>The content, or <see langword="null"/> for a retirement of <see cref="RecordKey"/>.</summary>
    public DocumentBody? Body { get; }

    /// <summary>The record the document belongs to. Always present on a retirement.</summary>
    public RecordKey? RecordKey { get; }

    /// <summary>The caller's idempotency key, or <see langword="null"/> to have one assigned.</summary>
    public DocumentId? IdempotencyId { get; }

    private IntakeDocument(DocumentBody? body, RecordKey? recordKey, DocumentId? idempotencyId)
    {
        Body = body;
        RecordKey = recordKey;
        IdempotencyId = idempotencyId;
    }

    /// <summary>Whether this is a retirement of the record named by <see cref="RecordKey"/>.</summary>
    public bool IsRetirement => Body is null;

    /// <summary>A document to accept, as <see cref="Ports.IIntakeService.AcceptAsync"/> takes it.</summary>
    public static IntakeDocument Accept(DocumentBody body, RecordKey? recordKey = null, DocumentId? idempotencyId = null)
    {
        ArgumentNullException.ThrowIfNull(body);
        return new IntakeDocument(body, recordKey, idempotencyId);
    }

    /// <summary>A retirement of <paramref name="recordKey"/>, as <see cref="Ports.IIntakeService.RetireAsync"/> takes it.</summary>
    public static IntakeDocument Retire(RecordKey recordKey, DocumentId? idempotencyId = null)
        => new(body: null, recordKey, idempotencyId);
}
