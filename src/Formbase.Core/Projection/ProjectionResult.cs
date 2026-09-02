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
/// <param name="UnresolvedReferences">
/// Declared columns whose <see cref="Schema.FieldBinding.Reference"/> binding the engine did not
/// resolve, in declared order. A reference reads true *now*, which the engine cannot yet evaluate,
/// so the column is left empty rather than filled with the document's own fixed-then copy — and
/// named here, because leaving it empty without saying so is the same silence in a quieter form.
/// </param>
public sealed record ProjectionResult(
    bool Projected,
    int Inserted,
    IReadOnlyList<ProjectionSkip> Skipped,
    IReadOnlyDictionary<string, int> AbsentFieldCounts,
    IReadOnlyList<string> UnresolvedReferences,
    Watermark ProjectedWatermark)
{
    private static readonly IReadOnlyDictionary<string, int> NoAbsences =
        new Dictionary<string, int>();

    /// <summary>No schema was proposed (e.g. no field hints yet); nothing was projected.</summary>
    public static ProjectionResult NoSchema() =>
        new(Projected: false, Inserted: 0, Array.Empty<ProjectionSkip>(), NoAbsences, [], Watermark.Zero);

    /// <summary>A projection completed, reaching <paramref name="watermark"/>.</summary>
    public static ProjectionResult Completed(
        int inserted,
        IReadOnlyList<ProjectionSkip> skipped,
        IReadOnlyDictionary<string, int> absentFieldCounts,
        IReadOnlyList<string> unresolvedReferences,
        Watermark watermark) =>
        new(Projected: true, inserted, skipped, absentFieldCounts, unresolvedReferences, watermark);

    /// <summary>
    /// Weighted Form Coverage Index for this run (Liolios et al. 2012 Metadata Coverage Index,
    /// weighted extension): wFCI = 1 - (Σᵢ wᵢ·absentᵢ) / (Inserted · Σᵢ wᵢ), row-averaged over every
    /// row this run inserted. <paramref name="declaredFields"/> must list every declared field to
    /// score, including ones with no entry in <see cref="AbsentFieldCounts"/> — a field absent from
    /// that dictionary was never absent from a row, not unscored, and still belongs in the total.
    /// A field with no entry in <paramref name="fieldWeights"/> gets weight 1; passing no weights at
    /// all reduces this exactly to the plain (unweighted) coverage ratio. Field-importance weights are
    /// a caller concern (e.g. derived from ablation) — this type only aggregates what it already has.
    /// </summary>
    public double WeightedFormCoverageIndex(
        IReadOnlyCollection<string> declaredFields,
        IReadOnlyDictionary<string, double>? fieldWeights = null)
    {
        if (Inserted == 0)
        {
            return double.NaN;
        }

        var weightedAbsent = 0.0;
        var totalWeight = 0.0;
        foreach (var field in declaredFields)
        {
            var weight = fieldWeights?.GetValueOrDefault(field, 1.0) ?? 1.0;
            totalWeight += weight;
            weightedAbsent += weight * AbsentFieldCounts.GetValueOrDefault(field, 0);
        }

        return totalWeight == 0 ? double.NaN : 1.0 - (weightedAbsent / (Inserted * totalWeight));
    }
}
