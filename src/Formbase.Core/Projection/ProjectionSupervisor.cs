using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// Composes a trigger's decision with the projector's action: one call evaluates whether a
/// projection is due and runs it if so. This is the unit a host cadence drives — a timer tick, a
/// queue message, an after-intake hook each call <see cref="RunOnceAsync"/>; the loop itself stays
/// with the host, because scheduling is an application concern, not an engine one.
/// </summary>
public sealed class ProjectionSupervisor
{
    private readonly IProjectionTrigger _trigger;
    private readonly IProjector _projector;

    public ProjectionSupervisor(IProjectionTrigger trigger, IProjector projector)
    {
        _trigger = trigger;
        _projector = projector;
    }

    /// <summary>
    /// Evaluates the trigger and, when a projection is due, runs it. Returns both the decision and
    /// the projection outcome (null when nothing ran).
    /// </summary>
    public async Task<ProjectionRunOutcome> RunOnceAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var decision = await _trigger.EvaluateAsync(type, cancellationToken).ConfigureAwait(false);
        if (!decision.ShouldProject)
        {
            return new ProjectionRunOutcome(decision, Projection: null);
        }

        var result = await _projector.ProjectAsync(type, cancellationToken).ConfigureAwait(false);
        return new ProjectionRunOutcome(decision, result);
    }
}

/// <summary>
/// One supervision pass: the trigger's <paramref name="Decision"/> and, when it fired, the
/// <paramref name="Projection"/> that ran (null when the policy held).
/// </summary>
public sealed record ProjectionRunOutcome(ProjectionTriggerDecision Decision, ProjectionResult? Projection);
