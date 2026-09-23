namespace Formbase.Core.Primitives;

/// <summary>
/// One page of a form type's raw stream, read in append order after a watermark.
/// <para>
/// <see cref="RawHead"/> is read before the page, and the page never reaches past it, so the two
/// describe the same moment: a caller has read everything there is when the last document's
/// watermark equals the head, and has more to read when it is lower. An empty page with the head at
/// or below the cursor means the caller is caught up.
/// </para>
/// </summary>
/// <param name="Documents">The documents after the cursor, oldest first, at most the requested count.</param>
/// <param name="RawHead">The form type's latest watermark when the page was read.</param>
public sealed record DocumentPage(IReadOnlyList<StoredDocument> Documents, Watermark RawHead);
