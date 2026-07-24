using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// What a completed projection materialized: the raw watermark it reached, the physical table it
/// built, and the fingerprint of the schema it built it from. Recorded by the projector via
/// <see cref="Ports.IProjectionState"/>; compared against the *current* declaration by
/// <see cref="ProjectionStatus.Evaluate"/> so a redeclaration without a re-projection cannot pass
/// as fresh (the watermark alone never moves on a redeclaration).
/// <para><see cref="Verified"/> is the integrity axis: a completed projection records it true, but
/// a failed rebuild whose cleanup also failed marks it false (best-effort) so a query refuses to
/// trust a possibly half-built table as fresh (design 2026-07-23 §6).</para>
/// </summary>
public sealed record ProjectionStamp(Watermark Watermark, string TableName, string SchemaFingerprint, bool Verified = true);
