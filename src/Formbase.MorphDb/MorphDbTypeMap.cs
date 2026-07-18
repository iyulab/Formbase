using Formbase.Core.Schema;

namespace Formbase.MorphDb;

/// <summary>
/// Maps formbase abstract column types to the type strings MorphDB's schema API accepts.
/// Notably, <see cref="ColumnType.Integer"/> maps to "bigint" (64-bit): formbase reads integer
/// fields as <see cref="long"/> and the system watermark column is a long, so mapping to MorphDB's
/// 32-bit "integer" would overflow.
/// </summary>
public static class MorphDbTypeMap
{
    public static string ToMorphType(ColumnType type) => type switch
    {
        ColumnType.Text => "text",
        ColumnType.Integer => "bigint",
        ColumnType.Decimal => "decimal",
        ColumnType.Boolean => "boolean",
        ColumnType.Timestamp => "timestamp",
        ColumnType.Uuid => "uuid",
        ColumnType.Jsonb => "jsonb",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped column type."),
    };
}
