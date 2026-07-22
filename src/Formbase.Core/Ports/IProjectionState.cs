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
}
