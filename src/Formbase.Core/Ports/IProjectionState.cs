using Formbase.Core.Primitives;

namespace Formbase.Core.Ports;

/// <summary>
/// Stores, per form type, the watermark a projection last reached (absent when never projected).
/// The derived <see cref="Projection.ProjectionStatus"/> — including staleness — is computed by
/// combining this with the raw head via <see cref="Projection.ProjectionStatus.Evaluate"/>.
/// </summary>
public interface IProjectionState
{
    /// <summary>The watermark the projection reached, or null if this form type was never projected.</summary>
    Task<Watermark?> GetProjectedWatermarkAsync(FormTypeRef type, CancellationToken cancellationToken = default);

    /// <summary>Records that a projection reached <paramref name="watermark"/>.</summary>
    Task SetProjectedAsync(FormTypeRef type, Watermark watermark, CancellationToken cancellationToken = default);

    /// <summary>Forgets any projection state for a form type (e.g. after its table is dropped).</summary>
    Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}
