using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Core.Ports;

/// <summary>
/// Stores, per form type, the <see cref="ProjectionStamp"/> of the last completed projection
/// (absent when never projected): the watermark it reached plus the table name and schema
/// fingerprint it materialized, and the <see cref="ProjectionSkip"/> records that run produced. The
/// derived <see cref="ProjectionStatus"/> — including staleness against both the raw head and the
/// current declaration — is computed by <see cref="ProjectionStatus.Evaluate"/>.
/// </summary>
/// <remarks>
/// The skips sit here rather than in a store of their own because they are part of the same fact:
/// what the last completed projection did. One write records both, one clear forgets both, and
/// there is no way to record a run while staying silent about what it dropped — which is what
/// <c>CONSTITUTION.md</c> means by calling a mapping failure a record rather than an exception.
/// They are a derivative, not a second source of truth: raw is the truth and re-projecting
/// regenerates them, which is why storing them adds no authority and why only the last run's are
/// kept.
/// </remarks>
public interface IProjectionState
{
    /// <summary>The stamp of the last completed projection, or null if this form type was never projected.</summary>
    Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that a projection completed with <paramref name="stamp"/>, together with the
    /// <paramref name="skips"/> it produced (empty when every document mapped). Replaces any
    /// previously recorded skips for this form type — a run's skips describe that run, and the
    /// previous run's answer to "what was dropped" stops being true the moment a new one completes.
    /// </summary>
    Task SetProjectedAsync(
        FormTypeRef type,
        ProjectionStamp stamp,
        IReadOnlyList<ProjectionSkip> skips,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// The skips recorded by the last completed projection, in the order the projector produced
    /// them; empty when that run skipped nothing, and also empty when this form type was never
    /// projected (<see cref="GetAsync"/> is what distinguishes those two — a caller that needs to
    /// tell them apart reads the stamp).
    /// </summary>
    Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets any projection state for a form type (e.g. after its table is dropped), including its
    /// recorded skips.
    /// </summary>
    Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks an existing stamp's integrity as unconfirmed (a no-op when none exists). The projector's
    /// best-effort fallback when a failed rebuild's <see cref="ClearAsync"/> also fails: the recorded
    /// stamp may now overclaim a half-built table as fresh, so a later query reads
    /// <see cref="Projection.ProjectionState.Unverified"/> and refuses rather than serving partial
    /// rows. Best-effort by nature — the outage that failed the clear may fail this too — but when it
    /// succeeds it closes the silent-wrong-answer window.
    /// </summary>
    Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}
