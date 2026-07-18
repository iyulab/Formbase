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

    /// <summary>The system columns, prepended to every projected table's domain columns.</summary>
    public static IReadOnlyList<ColumnDef> All { get; } =
    [
        new ColumnDef(DocumentId, ColumnType.Uuid, Nullable: false),
        new ColumnDef(Watermark, ColumnType.Integer, Nullable: false),
    ];
}
