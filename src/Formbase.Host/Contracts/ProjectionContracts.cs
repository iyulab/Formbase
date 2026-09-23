using Formbase.Core.Projection;
using Formbase.Host.Projection;

namespace Formbase.Host.Contracts;

/// <summary>
/// What a projection run reports. A run that found no declaration is not an error — the engine
/// accepts documents without one — so it answers with <c>projected: false</c> rather than a failure
/// the caller has to distinguish from a real one.
/// </summary>
/// <param name="Projected">False when no declaration proposed a schema, so nothing was built.</param>
/// <param name="Inserted">Rows that landed in the projected table.</param>
/// <param name="ProjectedWatermark">The raw position the run reached.</param>
/// <param name="Skipped">Documents that could not be mapped, with the reason for each.</param>
/// <param name="AbsentFieldCounts">
/// Per declared column, how many projected rows came from documents that did not carry the field at
/// all. An explicit null in a document is an answer and is not counted here — the projected NULL
/// conflates the two, and these counts are what makes the conflation visible.
/// </param>
/// <param name="UnresolvedReferences">
/// Declared columns left empty because their binding could not be resolved. Named rather than
/// silently blank: an empty column with no explanation reads as absent data.
/// </param>
/// <param name="NotProjectedReason">
/// Why nothing was built, when <see cref="Projected"/> is false; null when it is true. The three
/// diagnostic lists above describe a run that happened, so on a run that built nothing they are
/// empty — and empty reads as "nothing was lost". This is what says the run did not happen and
/// what would make it happen.
/// </param>
public sealed record ProjectionRunResponse(
    bool Projected,
    int Inserted,
    long ProjectedWatermark,
    IReadOnlyList<SkippedDocumentResponse> Skipped,
    IReadOnlyDictionary<string, int> AbsentFieldCounts,
    IReadOnlyList<string> UnresolvedReferences,
    NotProjectedReasonWire? NotProjectedReason);

/// <summary>
/// Why a projection run built nothing. Both mean no field hints are declared for the form type; they
/// differ in whether anything else could supply a shape, and so in what the caller does next.
/// </summary>
public enum NotProjectedReasonWire
{
    /// <summary>
    /// Nothing is declared and schema intelligence is not installed on this host, so a declaration is
    /// the only thing that can give the form type a shape. Running the projection again changes nothing.
    /// </summary>
    NoDeclaration,

    /// <summary>
    /// Nothing is declared, and the installed schema intelligence found nothing to infer a shape from —
    /// no documents yet, or none with an object body. Declare field hints, or append documents and run again.
    /// </summary>
    NothingToInfer,
}

/// <param name="DocumentId">The document that was not mapped.</param>
/// <param name="Reason">Why it could not be mapped into the declared shape.</param>
public sealed record SkippedDocumentResponse(Guid DocumentId, string Reason);

/// <summary>
/// What the last completed projection dropped. Unlike <see cref="LastRunResponse"/> — which is this
/// host instance's own memory of the run it performed — this is read from the recorded projection
/// state, so it survives a restart and answers for runs another instance performed.
/// </summary>
/// <param name="Skipped">
/// The skipped documents in the order the run produced them. Empty when that run mapped every
/// document, and also empty when the form type was never projected: read
/// <c>GET /formtypes/{type}/projection</c> to tell those apart.
/// </param>
/// <param name="Count">
/// How many entries <see cref="Skipped"/> holds. Named separately because the count is the first
/// thing a caller checking "did this run lose anything" reads, and making them count an array they
/// then discard is work the answer can do for them.
/// </param>
public sealed record ProjectionSkipsResponse(IReadOnlyList<SkippedDocumentResponse> Skipped, int Count);

/// <summary>
/// Whether a form type has a queryable projection and whether it can be trusted, with the two
/// watermarks that justify the answer.
/// </summary>
/// <param name="State">See <see cref="ProjectionStateWire"/> — a caller must branch on all four.</param>
/// <param name="ProjectedWatermark">The raw position the projection reached.</param>
/// <param name="RawHead">The current head of the form type's raw stream.</param>
/// <param name="LastRun">
/// What this host instance itself observed the last time it ran this projection — <c>null</c> when
/// this host has not run it since it started. Not a durable record: a different host instance, or
/// this one after a restart, answers <c>null</c> for a form type it has genuinely projected before.
/// Run the projection again to refresh it, the same way <see cref="State"/> itself is never stale
/// information you cannot correct.
/// </param>
public sealed record ProjectionStatusResponse(
    ProjectionStateWire State,
    long ProjectedWatermark,
    long RawHead,
    LastRunResponse? LastRun);

/// <param name="InsertedCount">Rows that landed in the projected table on that run.</param>
/// <param name="SkippedCount">Documents that could not be mapped on that run.</param>
/// <param name="ObservedAt">When this host observed the run.</param>
public sealed record LastRunResponse(int InsertedCount, int SkippedCount, DateTimeOffset ObservedAt)
{
    internal static LastRunResponse? FromTracked(LastProjectionRun? run) =>
        run is null ? null : new LastRunResponse(run.InsertedCount, run.SkippedCount, run.ObservedAt);
}

/// <summary>
/// The projection states as the wire names them. Declared here rather than serializing the engine's
/// own enum: the engine is free to rename or reorder its states, and a wire that followed would
/// change a published contract without anyone deciding to.
/// <para>
/// <c>unverified</c> is the value callers miss. A projection exists and names the current table, but
/// a failed rebuild left its integrity unconfirmed — treating it as <c>projected</c> means reading
/// rows nothing vouches for.
/// </para>
/// </summary>
public enum ProjectionStateWire
{
    /// <summary>No projected table exists for this form type.</summary>
    NotProjected,

    /// <summary>A projection exists and reflects the current raw head and declaration.</summary>
    Projected,

    /// <summary>A projection exists but raw documents, or the declaration, moved on after it ran.</summary>
    Stale,

    /// <summary>A projection exists but a failed rebuild left its integrity unconfirmed.</summary>
    Unverified,
}

internal static class ProjectionStateWireMapping
{
    /// <summary>
    /// Total by construction: no default arm, so a state added to the engine fails this build rather
    /// than reaching the wire under a name nobody chose. That is the whole reason the two enums are
    /// separate — the compiler is the only gate here whose reach is complete.
    /// </summary>
    // CS8524 asks for a fallback arm covering values outside the declared set — reachable only by
    // casting an arbitrary integer. Adding one would also swallow a state genuinely added to the
    // engine, which is the case this mapping exists to catch, so the warning is refused here
    // deliberately. An out-of-range cast still fails, at the point of the cast's own mistake.
#pragma warning disable CS8524
    public static ProjectionStateWire ToWire(this ProjectionState state) => state switch
    {
        ProjectionState.NotProjected => ProjectionStateWire.NotProjected,
        ProjectionState.Projected => ProjectionStateWire.Projected,
        ProjectionState.Stale => ProjectionStateWire.Stale,
        ProjectionState.Unverified => ProjectionStateWire.Unverified,
    };
#pragma warning restore CS8524
}
