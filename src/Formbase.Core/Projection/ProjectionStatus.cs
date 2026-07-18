using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// A form type's projection status: its state plus the watermarks that justify it.
/// </summary>
public sealed record ProjectionStatus(
    ProjectionState State,
    Watermark ProjectedWatermark,
    Watermark RawHead)
{
    /// <summary>
    /// Derives the status from the projected watermark (null when never projected) and the
    /// current raw head. Stale when the raw stream advanced past what was projected.
    /// </summary>
    public static ProjectionStatus Evaluate(Watermark? projectedWatermark, Watermark rawHead)
    {
        if (projectedWatermark is not { } projected)
        {
            return new ProjectionStatus(ProjectionState.NotProjected, Watermark.Zero, rawHead);
        }

        var state = rawHead > projected ? ProjectionState.Stale : ProjectionState.Projected;
        return new ProjectionStatus(state, projected, rawHead);
    }
}
