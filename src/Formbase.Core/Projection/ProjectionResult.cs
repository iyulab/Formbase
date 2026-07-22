using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// Outcome of a projection run. A run with no schema proposal is a no-op (<see cref="Projected"/> false);
/// otherwise it reports how many rows landed, which documents were skipped, per-column absence counts,
/// and the watermark reached.
/// </summary>
/// <param name="AbsentFieldCounts">
/// For each declared column, how many projected rows came from documents that did not carry the field
/// at all. An explicit <c>null</c> in the document is an answer and is not counted here — a field the
/// document never had is a different fact (the projected NULL conflates both; these counts make the
/// conflation visible per projection). Covers only rows that landed; skipped documents report their
/// own reasons via <see cref="Skipped"/>.
/// </param>
public sealed record ProjectionResult(
    bool Projected,
    int Inserted,
    IReadOnlyList<ProjectionSkip> Skipped,
    IReadOnlyDictionary<string, int> AbsentFieldCounts,
    Watermark ProjectedWatermark)
{
    private static readonly IReadOnlyDictionary<string, int> NoAbsences =
        new Dictionary<string, int>();

    /// <summary>No schema was proposed (e.g. no field hints yet); nothing was projected.</summary>
    public static ProjectionResult NoSchema() =>
        new(Projected: false, Inserted: 0, Array.Empty<ProjectionSkip>(), NoAbsences, Watermark.Zero);

    /// <summary>A projection completed, reaching <paramref name="watermark"/>.</summary>
    public static ProjectionResult Completed(
        int inserted,
        IReadOnlyList<ProjectionSkip> skipped,
        IReadOnlyDictionary<string, int> absentFieldCounts,
        Watermark watermark) =>
        new(Projected: true, inserted, skipped, absentFieldCounts, watermark);
}
