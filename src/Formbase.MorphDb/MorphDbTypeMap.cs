using Formbase.Core.Schema;

namespace Formbase.MorphDb;

/// <summary>
/// Maps formbase abstract column types to MorphDB's type names — the canonical ones, which MorphDB
/// also reports a column's type as, so a declared column and the column a table already has can be
/// compared by name (an in-place rebuild keeps a column whose shape is unchanged).
/// Notably, <see cref="ColumnType.Integer"/> maps to "biginteger" (64-bit): formbase reads integer
/// fields as <see cref="long"/> and the system watermark column is a long, so mapping to MorphDB's
/// 32-bit "integer" would overflow.
/// </summary>
public static class MorphDbTypeMap
{
    public static string ToMorphType(ColumnType type) => type switch
    {
        ColumnType.Text => "text",
        ColumnType.Integer => "biginteger",
        ColumnType.Decimal => "decimal",
        ColumnType.Boolean => "boolean",
        ColumnType.Timestamp => "datetime",
        ColumnType.Uuid => "uuid",
        ColumnType.Jsonb => "json",
        ColumnType.Date => "date",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped column type."),
    };
}
