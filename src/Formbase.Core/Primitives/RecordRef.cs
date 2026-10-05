namespace Formbase.Core.Primitives;

/// <summary>
/// Which record a projected row is: the document standing for it now, and the key that names the
/// record across corrections when it has one.
/// <para>
/// The two answer different questions. <see cref="Document"/> is where the row's values came from —
/// the raw document to read back for its full body — and it changes every time a keyed record is
/// corrected, because the projection shows each key's latest document. <see cref="Key"/> stays the
/// same through every correction, so it is what to remember a record by. A document without a key
/// is a record of its own and cannot be corrected, so for it the document is the identity and
/// <see cref="Key"/> is null.
/// </para>
/// </summary>
/// <param name="Document">The document the record stands on now.</param>
/// <param name="Key">The record's key, or null when the document is a record of its own.</param>
public readonly record struct RecordRef(DocumentId Document, RecordKey? Key);
