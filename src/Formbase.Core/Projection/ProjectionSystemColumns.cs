using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// System columns every projected row carries, so a record traces back to its raw source document.
/// </summary>
public static class ProjectionSystemColumns
{
    // A "fb_" prefix (not a leading underscore) avoids colliding with the backing database's own
    // reserved underscore-prefixed system columns (e.g. MorphDB's _version, _created_at).

    /// <summary>Column holding the source <see cref="Primitives.DocumentId"/>.</summary>
    public const string DocumentId = "fb_doc_id";

    /// <summary>Column holding the source <see cref="Primitives.Watermark"/> value.</summary>
    public const string Watermark = "fb_watermark";

    /// <summary>
    /// Column holding the source document's <see cref="Primitives.RecordKey"/> — which record a row is —
    /// or NULL for a document that is a record of its own.
    /// </summary>
    public const string RecordKey = "fb_record_key";

    /// <summary>
    /// Column holding the row's record identity as text — the value a
    /// <see cref="Schema.TargetLookup.Record"/> reference is matched against: the
    /// <see cref="Primitives.RecordKey"/> when the document has one, else its
    /// <see cref="Primitives.DocumentId"/> in canonical form (<c>D</c>, lower case). Stored rather than
    /// computed so every store matches a reference against one column with one equality — a store
    /// whose lookups compare a column to a column (MorphDB) cannot fold the two identities itself.
    /// </summary>
    public const string Record = "fb_record";

    /// <summary>The system columns, prepended to every projected table's domain columns.</summary>
    public static IReadOnlyList<ColumnDef> All { get; } =
    [
        new ColumnDef(DocumentId, ColumnType.Uuid, Nullable: false),
        new ColumnDef(Watermark, ColumnType.Integer, Nullable: false),
        new ColumnDef(RecordKey, ColumnType.Text, Nullable: true),
        new ColumnDef(Record, ColumnType.Text, Nullable: false),
    ];

    /// <summary>The <see cref="Record"/> value of a document: its key, else its document id in canonical form.</summary>
    public static string RecordOf(Primitives.DocumentId document, Primitives.RecordKey? key) =>
        key?.Value ?? document.Value.ToString("D");
}
