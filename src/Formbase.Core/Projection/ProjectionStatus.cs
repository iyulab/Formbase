using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// A form type's projection status: its state plus the watermarks that justify it.
/// </summary>
public sealed record ProjectionStatus(
    ProjectionState State,
    Watermark ProjectedWatermark,
    Watermark RawHead)
{
    /// <summary>
    /// Derives the status from the last projection's stamp (null when never projected), the current
    /// raw head, and the currently proposed schema (null when no declaration proposes one).
    /// <para>Freshness has two axes. Data: stale when the raw stream advanced past what was
    /// projected. Shape: stale when the declaration was re-declared (fingerprint drift) after the
    /// projection ran — the watermark never moves on a redeclaration, so it alone cannot see this.
    /// A declaration that moved to another table (or disappeared) is <see cref="ProjectionState.NotProjected"/>:
    /// the current declaration's table was never built, which is a projection gap, not staleness.</para>
    /// </summary>
    public static ProjectionStatus Evaluate(ProjectionStamp? stamp, Watermark rawHead, TableSchema? currentSchema)
    {
        if (stamp is null || currentSchema is null || stamp.TableName != currentSchema.TableName)
        {
            return new ProjectionStatus(ProjectionState.NotProjected, Watermark.Zero, rawHead);
        }

        if (!stamp.Verified)
        {
            // A failed rebuild left this stamp's integrity unconfirmed. It still names the current
            // table, so it is not NotProjected — but it cannot be trusted as fresh.
            return new ProjectionStatus(ProjectionState.Unverified, stamp.Watermark, rawHead);
        }

        var drifted = stamp.SchemaFingerprint != currentSchema.Fingerprint();
        var state = drifted || rawHead > stamp.Watermark ? ProjectionState.Stale : ProjectionState.Projected;
        return new ProjectionStatus(state, stamp.Watermark, rawHead);
    }
}
