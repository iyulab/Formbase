namespace Formbase.Core.Projection;

/// <summary>Why a trigger decided a projection should (or should not) run.</summary>
public enum ProjectionTriggerReason
{
    /// <summary>No projection is due: nothing proposable, nothing to project, or the policy is holding.</summary>
    None = 0,

    /// <summary>The current declaration's table was never built and documents are waiting.</summary>
    FirstProjection = 1,

    /// <summary>The declaration was re-declared after the last projection — the table serves a wrong shape until rebuilt.</summary>
    ShapeDrift = 2,

    /// <summary>The raw stream advanced past the projected watermark by at least the policy's threshold.</summary>
    DataLag = 3,

    /// <summary>A failed rebuild left the projection's integrity unconfirmed — rebuild to restore a verified state, no threshold applies.</summary>
    Unverified = 4,
}

/// <summary>
/// A trigger's verdict for one form type: the reason a projection is due (<see cref="ProjectionTriggerReason.None"/>
/// when it is not) plus the <see cref="ProjectionStatus"/> the verdict was derived from — so a
/// holding decision is observably a policy choice, never ignorance of staleness.
/// </summary>
public sealed record ProjectionTriggerDecision(ProjectionTriggerReason Reason, ProjectionStatus Status)
{
    /// <summary>Whether a projection should run now.</summary>
    public bool ShouldProject => Reason != ProjectionTriggerReason.None;
}
