using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// A document the projector could not map into the target schema. Recorded, not thrown:
/// a mapping failure skips one document and never corrupts the raw source of truth.
/// </summary>
public sealed record ProjectionSkip(DocumentId DocumentId, string Reason);
