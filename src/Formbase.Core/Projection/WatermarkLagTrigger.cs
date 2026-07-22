using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// First-cycle <see cref="IProjectionTrigger"/>: observes the gap between the raw head and the
/// recorded projection stamp. A shape change (redeclared fingerprint, moved table) fires
/// immediately — the projection is answering with the wrong shape until rebuilt. Pure data lag
/// fires only at <paramref name="lagThreshold"/> documents behind: a projection is a
/// drop-and-rebuild, so rebuilding on every single document would thrash; the threshold is the
/// policy knob between freshness and rebuild cost. Never fires when nothing proposes a schema
/// (projection would be a no-op) or when a declared type has no documents yet.
/// </summary>
public sealed class WatermarkLagTrigger : IProjectionTrigger
{
    private readonly IRawStore _rawStore;
    private readonly ISchemaProposer _proposer;
    private readonly IProjectionState _projectionState;
    private readonly long _lagThreshold;

    public WatermarkLagTrigger(
        IRawStore rawStore,
        ISchemaProposer proposer,
        IProjectionState projectionState,
        long lagThreshold = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(lagThreshold, 1);
        _rawStore = rawStore;
        _proposer = proposer;
        _projectionState = projectionState;
        _lagThreshold = lagThreshold;
    }

    public async Task<ProjectionTriggerDecision> EvaluateAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var stamp = await _projectionState.GetAsync(type, cancellationToken).ConfigureAwait(false);
        var schema = await _proposer.ProposeAsync(type, cancellationToken).ConfigureAwait(false);
        var rawHead = await _rawStore.HeadAsync(type, cancellationToken).ConfigureAwait(false);
        var status = ProjectionStatus.Evaluate(stamp, rawHead, schema);

        var reason = status.State switch
        {
            // No proposable schema: projecting is a no-op, whatever the raw stream holds. A declared
            // table that was never built fires only once documents exist — there is nothing to build from.
            ProjectionState.NotProjected when schema is not null && rawHead > Watermark.Zero
                => ProjectionTriggerReason.FirstProjection,

            // Stale covers two different urgencies. Shape drift means every already-projected row is
            // shaped wrong — no threshold applies. Data lag is quantitative and waits for the knob.
            ProjectionState.Stale when stamp!.SchemaFingerprint != schema!.Fingerprint()
                => ProjectionTriggerReason.ShapeDrift,
            ProjectionState.Stale when rawHead.Value - status.ProjectedWatermark.Value >= _lagThreshold
                => ProjectionTriggerReason.DataLag,

            _ => ProjectionTriggerReason.None,
        };

        return new ProjectionTriggerDecision(reason, status);
    }
}
