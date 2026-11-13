using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// Outcome of a projection run. A run with no schema proposal is a no-op (<see cref="Projected"/> false);
/// otherwise it reports how many rows landed, which documents were skipped, which optional fields were
/// emptied, per-column absence counts, and the watermark reached.
/// </summary>
/// <param name="Projected">
/// Whether the run projected anything; <see langword="false"/> when nothing proposed a schema.
/// </param>
/// <param name="Inserted">How many rows landed in the projection.</param>
/// <param name="Skipped">The documents the run did not project, each with its reason.</param>
/// <param name="SkippedFields">
/// Optional fields the run left empty in rows it did project, because the document's value could
/// not be converted to the column's type — each with the document, the column, and the reason. Not
/// counted in <see cref="Skipped"/> (the row landed) nor in <see cref="AbsentFieldCounts"/> (the
/// document did carry a value).
/// </param>
/// <param name="AbsentFieldCounts">
/// For each declared column, how many projected rows came from documents that did not carry the field
/// at all. An explicit <c>null</c> in the document is an answer and is not counted here — a field the
/// document never had is a different fact (the projected NULL conflates both; these counts make the
/// conflation visible per projection). A document that names its declaration (<see cref="StoredDocument.DeclarationSeq"/>)
/// is held to that declaration: a column it did not yet have is counted in <see cref="NotYetDeclaredFieldCounts"/>
/// instead, since a document cannot be absent a field nobody had asked for. Covers only rows that landed; skipped documents report their
/// own reasons via <see cref="Skipped"/>. A repeated section's table also counts, under its section field,
/// the parent documents that did not carry the section at all — an empty section is an answer (nothing
/// chosen) and is not counted.
/// </param>
/// <param name="UnresolvedReferences">
/// Declared <see cref="Schema.FieldBinding.Reference"/> columns that cannot be computed, in declared
/// order: those whose target names no way to find its record (no lookup and via field on the
/// <see cref="Schema.EntityRef"/>). A reference with that pair is computed by the store when the row is
/// read and never appears here. One without it reads empty rather than the document's own fixed-then
/// copy — and is named here, because leaving it empty without saying so is the same silence in a
/// quieter form. A declaration-level fact: the list is the same on every run until the declaration
/// changes.
/// </param>
/// <param name="ProjectedWatermark">The raw-stream watermark the run projected up to.</param>
public sealed record ProjectionResult(
    bool Projected,
    int Inserted,
    IReadOnlyList<ProjectionSkip> Skipped,
    IReadOnlyList<ProjectionFieldSkip> SkippedFields,
    IReadOnlyDictionary<string, int> AbsentFieldCounts,
    IReadOnlyList<string> UnresolvedReferences,
    Watermark ProjectedWatermark)
{
    /// <summary>
    /// Whether the run rebuilt the table or brought it forward (<see cref="ProjectionMode"/>). On an
    /// incremental run <see cref="Inserted"/>, <see cref="Skipped"/>, <see cref="SkippedFields"/> and
    /// <see cref="AbsentFieldCounts"/> cover the documents that run read — those appended since the last
    /// projection — while the table and the recorded skips cover everything.
    /// </summary>
    public ProjectionMode Mode { get; init; } = ProjectionMode.Rebuild;

    private static readonly IReadOnlyDictionary<string, int> NoAbsences =
        new Dictionary<string, int>();

    /// <summary>
    /// For each declared column, how many projected rows came from documents accepted under a declaration that did
    /// not yet have the column — the field was added after the document, so the document did not leave it out. Counted
    /// apart from <see cref="AbsentFieldCounts"/>, where these rows were counted before documents named their
    /// declaration. A document that names no declaration (accepted before the store kept one, or over a source with no
    /// history) is never counted here: its absences are in <see cref="AbsentFieldCounts"/> as they always were. A repeated section's
    /// table never counts here either — its documents name the parent's declaration, not the section's.
    /// </summary>
    public IReadOnlyDictionary<string, int> NotYetDeclaredFieldCounts { get; init; } = NoAbsences;

    /// <summary>No schema was proposed (e.g. no field hints yet); nothing was projected.</summary>
    public static ProjectionResult NoSchema() =>
        new(Projected: false, Inserted: 0, Array.Empty<ProjectionSkip>(), [], NoAbsences, [], Watermark.Zero);

    /// <summary>A projection completed, reaching <paramref name="watermark"/>.</summary>
    public static ProjectionResult Completed(
        int inserted,
        IReadOnlyList<ProjectionSkip> skipped,
        IReadOnlyList<ProjectionFieldSkip> skippedFields,
        IReadOnlyDictionary<string, int> absentFieldCounts,
        IReadOnlyList<string> unresolvedReferences,
        Watermark watermark) =>
        new(Projected: true, inserted, skipped, skippedFields, absentFieldCounts, unresolvedReferences, watermark);

    /// <summary>
    /// Weighted Form Coverage Index for this run (Liolios et al. 2012 Metadata Coverage Index,
    /// weighted extension): wFCI = 1 - (Σᵢ wᵢ·absentᵢ) / (Inserted · Σᵢ wᵢ), row-averaged over every
    /// row this run inserted. <paramref name="declaredFields"/> must list every declared field to
    /// score, including ones with no entry in <see cref="AbsentFieldCounts"/> — a field absent from
    /// that dictionary was never absent from a row, not unscored, and still belongs in the total.
    /// A row from a document accepted before a field was declared does not count that field in the denominator
    /// (<see cref="NotYetDeclaredFieldCounts"/>) — the document had no chance to carry it. With none such, this is
    /// exactly the formula above.
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
        var weightedRows = 0.0;
        foreach (var field in declaredFields)
        {
            var weight = fieldWeights?.GetValueOrDefault(field, 1.0) ?? 1.0;
            weightedRows += weight * (Inserted - NotYetDeclaredFieldCounts.GetValueOrDefault(field, 0));
            weightedAbsent += weight * AbsentFieldCounts.GetValueOrDefault(field, 0);
        }

        return weightedRows == 0 ? double.NaN : 1.0 - (weightedAbsent / weightedRows);
    }
}
