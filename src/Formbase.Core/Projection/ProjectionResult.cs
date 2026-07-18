using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// Outcome of a projection run. A run with no schema proposal is a no-op (<see cref="Projected"/> false);
/// otherwise it reports how many rows landed, which documents were skipped, and the watermark reached.
/// </summary>
public sealed record ProjectionResult(
    bool Projected,
    int Inserted,
    IReadOnlyList<ProjectionSkip> Skipped,
    Watermark ProjectedWatermark)
{
    /// <summary>No schema was proposed (e.g. no field hints yet); nothing was projected.</summary>
    public static ProjectionResult NoSchema() =>
        new(Projected: false, Inserted: 0, Array.Empty<ProjectionSkip>(), Watermark.Zero);

    /// <summary>A projection completed, reaching <paramref name="watermark"/>.</summary>
    public static ProjectionResult Completed(int inserted, IReadOnlyList<ProjectionSkip> skipped, Watermark watermark) =>
        new(Projected: true, inserted, skipped, watermark);
}
