using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Core.Ports;

/// <summary>
/// Stores, per form type, the <see cref="ProjectionStamp"/> of the last completed projection
/// (absent when never projected): the watermark it reached plus the table name and schema
/// fingerprint it materialized. The derived <see cref="ProjectionStatus"/> — including staleness
/// against both the raw head and the current declaration — is computed by
/// <see cref="ProjectionStatus.Evaluate"/>.
/// </summary>
public interface IProjectionState
{
    /// <summary>The stamp of the last completed projection, or null if this form type was never projected.</summary>
    Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default);

    /// <summary>Records that a projection completed with <paramref name="stamp"/>.</summary>
    Task SetProjectedAsync(FormTypeRef type, ProjectionStamp stamp, CancellationToken cancellationToken = default);

    /// <summary>Forgets any projection state for a form type (e.g. after its table is dropped).</summary>
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
