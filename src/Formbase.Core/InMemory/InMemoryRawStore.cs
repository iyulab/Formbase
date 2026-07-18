using System.Runtime.CompilerServices;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.InMemory;

/// <summary>
/// In-process, append-only <see cref="IRawStore"/>. Serves as both the reference implementation
/// (against which the raw-store contract is defined) and a usable single-process store.
/// Watermarks are globally monotonic, giving a total append order across all form types.
/// </summary>
public sealed class InMemoryRawStore : IRawStore
{
    private readonly Lock _gate = new();
    private readonly List<StoredDocument> _log = [];
    private readonly Dictionary<DocumentId, StoredDocument> _byId = [];
    private readonly TimeProvider _clock;
    private long _sequence;

    public InMemoryRawStore(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            // Idempotent by id: re-appending a known id returns the original, first-written document.
            if (_byId.TryGetValue(id, out var existing))
            {
                return Task.FromResult(existing);
            }

            var stored = new StoredDocument(id, type, body, new Watermark(++_sequence), _clock.GetUtcNow());
            _log.Add(stored);
            _byId[id] = stored;
            return Task.FromResult(stored);
        }
    }

    public Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _byId.TryGetValue(id, out var stored);
            return Task.FromResult(stored);
        }
    }

    public async IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        List<StoredDocument> snapshot;
        lock (_gate)
        {
            snapshot = _log.Where(d => d.Type == type && d.Watermark > after).ToList();
        }

        foreach (var stored in snapshot)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return stored;
        }

        await Task.CompletedTask.ConfigureAwait(false);
    }

    public Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var head = _log
                .Where(d => d.Type == type)
                .Select(d => d.Watermark)
                .DefaultIfEmpty(Watermark.Zero)
                .Max();
            return Task.FromResult(head);
        }
    }
}
