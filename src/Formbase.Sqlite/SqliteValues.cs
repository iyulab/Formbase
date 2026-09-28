using System.Globalization;
using Formbase.Core.Schema;

namespace Formbase.Sqlite;

/// <summary>
/// How each <see cref="ColumnType"/> is kept in SQLite and read back. SQLite has five storage classes,
/// so three types are kept as text in a form whose SQLite comparison is the type's own order:
/// decimals as exact invariant text under the <see cref="DecimalCollation"/> (numeric order and
/// equality), instants as fixed-width UTC text (lexical order is chronological order), and ids as
/// lowercase text. Booleans are 0/1 integers. Reading back uses the declared type, so a row carries the
/// same CLR values the in-memory store holds.
/// </summary>
internal static class SqliteValues
{
    public const string DecimalCollation = "fb_decimal";

    private const string InstantFormat = "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'";

    /// <summary>The column's SQLite declaration: storage class plus, for decimals, its collation.</summary>
    public static string Declaration(ColumnType type) => type switch
    {
        ColumnType.Text => "TEXT",
        ColumnType.Integer => "INTEGER",
        ColumnType.Decimal => $"TEXT COLLATE {DecimalCollation}",
        ColumnType.Boolean => "INTEGER",
        ColumnType.Timestamp => "TEXT",
        ColumnType.Uuid => "TEXT",
        ColumnType.Jsonb => "TEXT",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unmapped column type."),
    };

    /// <summary>
    /// A value as stored for a column of <paramref name="type"/>. A value of another type than the
    /// column's is stored as its invariant text, so it compares unequal rather than failing — the same
    /// outcome a mismatched value has on every other store.
    /// </summary>
    public static object ToStorage(object? value, ColumnType type) => value switch
    {
        null => DBNull.Value,
        long or int or short when type == ColumnType.Integer => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        decimal number when type == ColumnType.Decimal => number.ToString(CultureInfo.InvariantCulture),
        bool flag when type == ColumnType.Boolean => flag ? 1L : 0L,
        DateTimeOffset instant when type == ColumnType.Timestamp => instant.UtcDateTime.ToString(InstantFormat, CultureInfo.InvariantCulture),
        DateTime instant when type == ColumnType.Timestamp => instant.ToUniversalTime().ToString(InstantFormat, CultureInfo.InvariantCulture),
        Guid id when type == ColumnType.Uuid => id.ToString("D"),
        string text => text,
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    /// <summary>A stored value read back as the CLR value of the column's declared type.</summary>
    public static object? FromStorage(object? stored, ColumnType type)
    {
        if (stored is null or DBNull)
        {
            return null;
        }

        return type switch
        {
            ColumnType.Integer => Convert.ToInt64(stored, CultureInfo.InvariantCulture),
            ColumnType.Decimal => decimal.Parse(Convert.ToString(stored, CultureInfo.InvariantCulture)!, NumberStyles.Number, CultureInfo.InvariantCulture),
            ColumnType.Boolean => Convert.ToInt64(stored, CultureInfo.InvariantCulture) != 0,
            ColumnType.Timestamp => DateTimeOffset.ParseExact((string)stored, InstantFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal),
            ColumnType.Uuid => Guid.Parse((string)stored),
            _ => Convert.ToString(stored, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>A name as a quoted SQLite identifier.</summary>
    public static string Quote(string identifier) => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
