using Formbase.Core.Projection;

namespace Formbase.Core.Primitives;

/// <summary>
/// A page of a repeated section's rows, read from its parent's raw stream: each parent document of the page with the rows
/// it holds for the section. A section has no documents of its own, so its watermarks and its head are the parent's.
/// </summary>
/// <param name="Documents">The parent's documents after the requested watermark, oldest first, each with what it holds for the section.</param>
/// <param name="RawHead">The parent stream's head when the page was read.</param>
public sealed record SectionRowPage(IReadOnlyList<SectionDocumentRows> Documents, Watermark RawHead);

/// <summary>A parent document and the rows it holds for a section.</summary>
/// <param name="Document">The parent document as stored.</param>
/// <param name="Expansion">The rows, or why they cannot be read; null for a retirement, which holds none.</param>
public sealed record SectionDocumentRows(StoredDocument Document, SectionExpansion? Expansion);
