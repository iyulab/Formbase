using Formbase.Core.Ports;
using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// First-cycle <see cref="IProjectionTrigger"/>: observes the gap between the raw head and the
/// recorded projection stamp. A shape change (redeclared fingerprint, moved table) fires
/// immediately — the projection is answering with the wrong shape until rebuilt. Pure data lag
/// fires only at <c>lagThreshold</c> of this form type's documents behind — counted, not read off the
/// watermarks, which are global across form types and so also move for every other type's documents.
/// A run that only brings the table forward costs what was appended, so the default of one keeps a
/// projection current; a higher threshold batches runs. Never fires when nothing proposes a schema
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

            // A failed rebuild left the projection unverified — rebuild to restore integrity, no
            // threshold applies. Without this the automation would never repair a suspect projection.
            ProjectionState.Unverified when schema is not null
                => ProjectionTriggerReason.Unverified,

            // Stale covers two different urgencies. Shape drift means every already-projected row is
            // shaped wrong — no threshold applies. Data lag is quantitative and waits for the knob.
            ProjectionState.Stale when stamp!.SchemaFingerprint != ProjectedShape.Fingerprint(schema!)
                => ProjectionTriggerReason.ShapeDrift,
            ProjectionState.Stale when await IsBehindAsync(type, status.ProjectedWatermark, rawHead, cancellationToken).ConfigureAwait(false)
                => ProjectionTriggerReason.DataLag,

            _ => ProjectionTriggerReason.None,
        };

        return new ProjectionTriggerDecision(reason, status);
    }

    /// <summary>
    /// Whether at least <c>lagThreshold</c> of this form type's documents lie after the projected
    /// watermark. Reads no more than that many, so the check costs the threshold, not the backlog.
    /// </summary>
    private async Task<bool> IsBehindAsync(FormTypeRef type, Watermark projected, Watermark rawHead, CancellationToken cancellationToken)
    {
        long behind = 0;
        await foreach (var document in _rawStore.StreamAsync(type, projected, cancellationToken).ConfigureAwait(false))
        {
            if (document.Watermark > rawHead)
            {
                break;
            }

            if (++behind >= _lagThreshold)
            {
                return true;
            }
        }

        return false;
    }
}
