using System.Diagnostics.CodeAnalysis;

namespace Formbase.Core.Schema;

/// <summary>
/// Abstract column types a projection can request. These map onto MorphDB's logical types
/// at the adapter boundary; the core never speaks physical SQL types.
/// </summary>
[SuppressMessage(
    "Naming",
    "CA1720:Identifier contains type name",
    Justification = "Integer/Decimal are the canonical abstract column-type vocabulary (mirrors SQL/MorphDB logical types); renaming would obscure the mapping.")]
public enum ColumnType
{
    Text,
    Integer,
    Decimal,
    Boolean,
    Timestamp,
    Uuid,
    Jsonb,

    /// <summary>
    /// A calendar date — a day, not a point in time: <c>2026-10-21</c> is the same day in every offset.
    /// Values are <see cref="DateOnly"/>; a document carries one as ISO 8601 text (<c>yyyy-MM-dd</c>).
    /// </summary>
    Date,
}
