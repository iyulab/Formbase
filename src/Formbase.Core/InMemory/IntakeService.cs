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

    public async Task<DocumentId> AcceptAsync(
        FormTypeRef type,
        DocumentBody body,
        DocumentId? idempotencyId = null,
        CancellationToken cancellationToken = default)
    {
        var id = idempotencyId ?? DocumentId.New();

        try
        {
            var stored = await _rawStore.AppendAsync(type, id, body, cancellationToken).ConfigureAwait(false);
            return stored.Id;
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not FormbaseException)
        {
            throw new IntakeException($"Failed to accept document for form type '{type}'.", ex);
        }
    }
}
