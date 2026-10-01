using System.Collections.Concurrent;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Core.InMemory;

/// <summary>In-process <see cref="IProjectionState"/> backed by a concurrent map.</summary>
public sealed class InMemoryProjectionState : IProjectionState
{
    private readonly ConcurrentDictionary<FormTypeRef, ProjectionStamp> _stamps = new();
    private readonly ConcurrentDictionary<FormTypeRef, IReadOnlyList<ProjectionSkip>> _skips = new();
    private readonly ConcurrentDictionary<FormTypeRef, IReadOnlyList<ProjectionFieldSkip>> _fieldSkips = new();

    // Every write takes it, so a delta's compare-and-set and the writes it guards are one step.
    private readonly Lock _deltaGate = new();

    public Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_stamps.TryGetValue(type, out var stamp) ? stamp : null);

    public Task SetProjectedAsync(
        FormTypeRef type,
        ProjectionStamp stamp,
        IReadOnlyList<ProjectionSkip> skips,
        IReadOnlyList<ProjectionFieldSkip> fieldSkips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skips);
        ArgumentNullException.ThrowIfNull(fieldSkips);

        // Copied rather than stored by reference: the projector builds the list it hands over and is
        // free to keep mutating its own, and a stored view that changed afterwards would report a
        // run that never happened.
        lock (_deltaGate)
        {
            _stamps[type] = stamp;
            _skips[type] = [.. skips];
            _fieldSkips[type] = [.. fieldSkips];
        }

        return Task.CompletedTask;
    }

    public Task<bool> ApplyProjectedDeltaAsync(
        FormTypeRef type,
        Watermark expectedWatermark,
        ProjectionStamp stamp,
        IReadOnlyCollection<RecordKey> withdrawnKeys,
        IReadOnlyCollection<DocumentId> withdrawnDocuments,
        IReadOnlyList<ProjectionSkip> addedSkips,
        IReadOnlyList<ProjectionFieldSkip> addedFieldSkips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stamp);
        ArgumentNullException.ThrowIfNull(addedSkips);
        ArgumentNullException.ThrowIfNull(addedFieldSkips);

        var keys = withdrawnKeys.ToHashSet();
        var documents = withdrawnDocuments.ToHashSet();
        bool Withdrawn(DocumentId document, RecordKey? key) => documents.Contains(document) || (key is { } k && keys.Contains(k));

        // One lock across the check and the three writes, so the compare-and-set holds against another
        // delta or a full run recording at the same moment.
        lock (_deltaGate)
        {
            if (!_stamps.TryGetValue(type, out var current) || current.Watermark != expectedWatermark || !current.Verified)
            {
                return Task.FromResult(false);
            }

            _stamps[type] = stamp;
            _skips[type] = [.. (_skips.TryGetValue(type, out var skips) ? skips : []).Where(s => !Withdrawn(s.DocumentId, s.Key)), .. addedSkips];
            _fieldSkips[type] = [.. (_fieldSkips.TryGetValue(type, out var fieldSkips) ? fieldSkips : []).Where(s => !Withdrawn(s.DocumentId, s.Key)), .. addedFieldSkips];
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_skips.TryGetValue(type, out var skips) ? skips : []);

    public Task<IReadOnlyList<ProjectionFieldSkip>> GetFieldSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_fieldSkips.TryGetValue(type, out var fieldSkips) ? fieldSkips : []);

    public Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        lock (_deltaGate)
        {
            _stamps.TryRemove(type, out _);
            _skips.TryRemove(type, out _);
            _fieldSkips.TryRemove(type, out _);
        }

        return Task.CompletedTask;
    }

    public Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        // Only flip a stamp that exists — a type never projected has nothing to overclaim.
        lock (_deltaGate)
        {
            if (_stamps.TryGetValue(type, out var stamp))
            {
                _stamps[type] = stamp with { Verified = false };
            }
        }

        return Task.CompletedTask;
    }
}
