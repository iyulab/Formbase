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
    /// or NULL for a document that is a record of its own. A repeated section's row carries its parent's
    /// (<see cref="Schema.TableSchema.Section"/>): the row is part of the parent's record, and goes when it does.
    /// </summary>
    public const string RecordKey = "fb_record_key";

    /// <summary>
    /// Column holding the row's record identity as text — the value a
    /// <see cref="Schema.TargetLookup.Record"/> reference is matched against: the
    /// <see cref="Primitives.RecordKey"/> when the document has one, else its
    /// <see cref="Primitives.DocumentId"/> in canonical form (<c>D</c>, lower case). Stored rather than
    /// computed so every store matches a reference against one column with one equality — a store
    /// whose lookups compare a column to a column (MorphDB) cannot fold the two identities itself. A
    /// repeated section's row is its parent's identity, a slash, and its position (<see cref="SectionRow.Record"/>).
    /// </summary>
    public const string Record = "fb_record";

    /// <summary>
    /// Column holding a repeated section row's position in its parent's section array, from 0 — present
    /// only on a section's table (<see cref="Schema.TableSchema.Section"/>). The rows a parent holds share its
    /// document and watermark, so this is what orders them and tells them apart.
    /// </summary>
    public const string Row = "fb_row";

    /// <summary>The system columns, prepended to every projected table's domain columns.</summary>
    public static IReadOnlyList<ColumnDef> All { get; } =
    [
        new ColumnDef(DocumentId, ColumnType.Uuid, Nullable: false),
        new ColumnDef(Watermark, ColumnType.Integer, Nullable: false),
        new ColumnDef(RecordKey, ColumnType.Text, Nullable: true),
        new ColumnDef(Record, ColumnType.Text, Nullable: false),
    ];

    private static IReadOnlyList<ColumnDef> WithRow { get; } = [.. All, new ColumnDef(Row, ColumnType.Integer, Nullable: false)];

    /// <summary>
    /// The system columns <paramref name="schema"/>'s table carries: <see cref="All"/>, and <see cref="Row"/>
    /// when it is a repeated section. A table that is not a section has the columns it always had.
    /// </summary>
    public static IReadOnlyList<ColumnDef> Of(TableSchema schema) =>
        (schema ?? throw new ArgumentNullException(nameof(schema))).Section is null ? All : WithRow;

    /// <summary>
    /// The read-only column a store answers a reference field's source in, when a query asks for it
    /// (<see cref="Query.QuerySpec.ReferenceSources"/>): the target row's document id, or null when no
    /// target row was read. Never stored — a store computes it with the reference, from the same row.
    /// </summary>
    public static string SourceDocumentOf(string referenceColumn) => "fb_src_doc__" + referenceColumn;

    /// <summary>The target row's record key, beside <see cref="SourceDocumentOf"/>; null for a record of its own.</summary>
    public static string SourceKeyOf(string referenceColumn) => "fb_src_key__" + referenceColumn;

    /// <summary>
    /// Whether the target can answer the reference at all — its table exists with the column read and
    /// the column matched — beside <see cref="SourceDocumentOf"/>: a boolean, or an integer 1 or 0.
    /// </summary>
    public static string SourceAnswersOf(string referenceColumn) => "fb_src_ans__" + referenceColumn;

    /// <summary>The <see cref="Record"/> value of a document: its key, else its document id in canonical form.</summary>
    public static string RecordOf(Primitives.DocumentId document, Primitives.RecordKey? key) =>
        key?.Value ?? document.Value.ToString("D");
}
