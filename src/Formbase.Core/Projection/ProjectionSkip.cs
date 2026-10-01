using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// A document the projector could not map into the target schema. Recorded, not thrown:
/// a mapping failure skips one document and never corrupts the raw source of truth.
/// </summary>
/// <param name="DocumentId">The document that was skipped.</param>
/// <param name="Reason">Why it could not be mapped.</param>
/// <param name="Key">
/// The record the document stood for, or null for a document that is a record of its own — what lets a
/// later correction of that record withdraw this skip without rebuilding the projection.
/// </param>
public sealed record ProjectionSkip(DocumentId DocumentId, string Reason, RecordKey? Key = null);
