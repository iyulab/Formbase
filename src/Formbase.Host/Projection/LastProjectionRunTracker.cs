using System.Collections.Concurrent;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Host.Projection;

/// <summary>
/// The host's own memory of each form type's most recently completed projection run — inserted and
/// skipped counts, not persisted, lost on restart.
/// <para>
/// Exists so a caller reading projection status later can tell "this run skipped nothing" from "no
/// idea what the last run did" without the engine gaining a public field for it. Raw is the source of
/// truth and a projection is a rebuildable derivative of it (<c>Formbase.Core</c>'s own framing), so
/// this observation is exactly that: rebuildable by running a projection again, not a second store of
/// anything durable — which is what keeps it a host-response addition instead of a core surface
/// change.
/// </para>
/// </summary>
public sealed class LastProjectionRunTracker
{
    private readonly ConcurrentDictionary<FormTypeRef, LastProjectionRun> _runs = new();

    /// <summary>
    /// Records a completed run. A run that found no schema to project
    /// (<see cref="ProjectionResult.Projected"/> false) leaves any prior observation in place — it
    /// did not touch the table, so overwriting would replace a real observation with silence.
    /// </summary>
    public void Record(FormTypeRef type, ProjectionResult result)
    {
        if (!result.Projected)
        {
            return;
        }

        _runs[type] = new LastProjectionRun(result.Inserted, result.Skipped.Count, DateTimeOffset.UtcNow);
    }

    public LastProjectionRun? TryGet(FormTypeRef type) =>
        _runs.TryGetValue(type, out var run) ? run : null;
}

/// <param name="InsertedCount">Rows that landed in the projected table on that run.</param>
/// <param name="SkippedCount">Documents that could not be mapped on that run.</param>
/// <param name="ObservedAt">When this host observed the run — not when raw last changed.</param>
public sealed record LastProjectionRun(int InsertedCount, int SkippedCount, DateTimeOffset ObservedAt);
