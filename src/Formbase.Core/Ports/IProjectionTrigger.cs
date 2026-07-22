using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Core.Ports;

/// <summary>
/// The seam where projection-automation policy plugs in: decides whether a form type's projection
/// should run <em>now</em>. Pure decision — it never projects; pair it with an
/// <see cref="IProjector"/> (see <see cref="ProjectionSupervisor"/>) and drive it from whatever
/// cadence the host owns (a timer, a queue, an intake hook). The first-cycle implementation is
/// <see cref="WatermarkLagTrigger"/>, which observes the gap between the raw head and the recorded
/// projection stamp.
/// </summary>
public interface IProjectionTrigger
{
    Task<ProjectionTriggerDecision> EvaluateAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}
