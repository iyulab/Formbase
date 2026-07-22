using System.Collections.Concurrent;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Core.InMemory;

/// <summary>In-process <see cref="IProjectionState"/> backed by a concurrent map.</summary>
public sealed class InMemoryProjectionState : IProjectionState
{
    private readonly ConcurrentDictionary<FormTypeRef, ProjectionStamp> _stamps = new();

    public Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_stamps.TryGetValue(type, out var stamp) ? stamp : null);

    public Task SetProjectedAsync(FormTypeRef type, ProjectionStamp stamp, CancellationToken cancellationToken = default)
    {
        _stamps[type] = stamp;
        return Task.CompletedTask;
    }

    public Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        _stamps.TryRemove(type, out _);
        return Task.CompletedTask;
    }
}
