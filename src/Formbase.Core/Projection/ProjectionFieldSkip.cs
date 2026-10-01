using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// An optional field the projector could not convert, so it left that one box empty and kept the
/// row. A required field that fails the same way still skips the whole document
/// (<see cref="ProjectionSkip"/>) — the row cannot stand without it; an optional one can, and hiding
/// the whole document over it would trade one empty box for a missing row.
/// </summary>
/// <remarks>
/// Distinct from an absent field (<see cref="ProjectionResult.AbsentFieldCounts"/>): the document
/// did carry a value here, just not one the declared column type can hold. The emptied box is a
/// value the run dropped, so it is recorded the way a skipped document is — which document, which
/// field, why — and can be fixed in the document and re-projected.
/// </remarks>
/// <param name="DocumentId">The document whose field was emptied; its row was projected.</param>
/// <param name="Field">The projected column left empty.</param>
/// <param name="Reason">Why the value could not be converted to the column's type.</param>
public sealed record ProjectionFieldSkip(DocumentId DocumentId, string Field, string Reason);
