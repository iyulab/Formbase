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

    public Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_stamps.TryGetValue(type, out var stamp) ? stamp : null);

    public Task SetProjectedAsync(
        FormTypeRef type,
        ProjectionStamp stamp,
        IReadOnlyList<ProjectionSkip> skips,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(skips);

        // Copied rather than stored by reference: the projector builds the list it hands over and is
        // free to keep mutating its own, and a stored view that changed afterwards would report a
        // run that never happened.
        _stamps[type] = stamp;
        _skips[type] = [.. skips];
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_skips.TryGetValue(type, out var skips) ? skips : []);

    public Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        _stamps.TryRemove(type, out _);
        _skips.TryRemove(type, out _);
        return Task.CompletedTask;
    }

    public Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        // Only flip a stamp that exists — a type never projected has nothing to overclaim.
        if (_stamps.TryGetValue(type, out var stamp))
        {
            _stamps.TryUpdate(type, stamp with { Verified = false }, stamp);
        }

        return Task.CompletedTask;
    }
}
