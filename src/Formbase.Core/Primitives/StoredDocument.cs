namespace Formbase.Core.Primitives;

/// <summary>
/// A document as it lives in the raw store — the source of truth. Append-only: a correction
/// is a new append (new id, later watermark), never an in-place mutation.
/// </summary>
public sealed record StoredDocument(
    DocumentId Id,
    FormTypeRef Type,
    DocumentBody Body,
    Watermark Watermark,
    DateTimeOffset AppendedAt);
