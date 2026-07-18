using System.Collections.Concurrent;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.InMemory;

/// <summary>In-process <see cref="IProjectionState"/> backed by a concurrent map.</summary>
public sealed class InMemoryProjectionState : IProjectionState
{
    private readonly ConcurrentDictionary<FormTypeRef, Watermark> _watermarks = new();

    public Task<Watermark?> GetProjectedWatermarkAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        => Task.FromResult(_watermarks.TryGetValue(type, out var watermark) ? watermark : (Watermark?)null);

    public Task SetProjectedAsync(FormTypeRef type, Watermark watermark, CancellationToken cancellationToken = default)
    {
        _watermarks[type] = watermark;
        return Task.CompletedTask;
    }

    public Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        _watermarks.TryRemove(type, out _);
        return Task.CompletedTask;
    }
}
