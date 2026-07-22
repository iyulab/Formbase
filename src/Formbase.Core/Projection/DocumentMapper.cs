using System.Globalization;
using System.Text.Json;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// Maps a stored document into a projected row against a given domain schema. This is deterministic
/// value coercion, not type inference: the schema is already known (from hints), and each field is
/// read and converted per its declared column type. A required field that is missing or unconvertible
/// makes the whole document unmappable — reported as a skip, never a raw-store mutation.
/// </summary>
internal static class DocumentMapper
{
    public static bool TryMap(
        StoredDocument document,
        IReadOnlyList<ColumnDef> domainColumns,
        out IReadOnlyDictionary<string, object?> row,
        out IReadOnlyList<string> absentFields,
        out string reason)
    {
        var mapped = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ProjectionSystemColumns.DocumentId] = document.Id.Value,
            [ProjectionSystemColumns.Watermark] = document.Watermark.Value,
        };

        List<string>? absent = null;
        var root = document.Body.Root;
        foreach (var column in domainColumns)
        {
            if (!TryConvert(root, column, out var value, out var fieldAbsent, out reason))
            {
                row = mapped;
                absentFields = [];
                return false;
            }

            if (fieldAbsent)
            {
                (absent ??= []).Add(column.Name);
            }

            mapped[column.Name] = value;
        }

        row = mapped;
        absentFields = absent ?? (IReadOnlyList<string>)[];
        reason = string.Empty;
        return true;
    }

    private static bool TryConvert(JsonElement root, ColumnDef column, out object? value, out bool absent, out string reason)
    {
        value = null;
        reason = string.Empty;

        var present = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(column.Name, out var element);
        var field = present ? root.GetProperty(column.Name) : default;
        // A field the document never had is a different fact from an explicit null — the projected
        // NULL conflates both, so the distinction is surfaced (absence counts, skip reasons).
        absent = !present;

        if (!present || field.ValueKind == JsonValueKind.Null)
        {
            if (column.Nullable)
            {
                return true;
            }

            reason = present
                ? $"required field '{column.Name}' is null"
                : $"required field '{column.Name}' is absent from the document";
            return false;
        }

        switch (column.Type)
        {
            case ColumnType.Text:
                value = field.ValueKind == JsonValueKind.String ? field.GetString() : field.GetRawText();
                return true;

            case ColumnType.Integer:
                if (field.ValueKind == JsonValueKind.Number && field.TryGetInt64(out var i))
                {
                    value = i;
                    return true;
                }
                break;

            case ColumnType.Decimal:
                if (field.ValueKind == JsonValueKind.Number && field.TryGetDecimal(out var d))
                {
                    value = d;
                    return true;
                }
                break;

            case ColumnType.Boolean:
                if (field.ValueKind is JsonValueKind.True or JsonValueKind.False)
                {
                    value = field.GetBoolean();
                    return true;
                }
                break;

            case ColumnType.Timestamp:
                if (field.ValueKind == JsonValueKind.String
                    && DateTimeOffset.TryParse(field.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var ts))
                {
                    value = ts;
                    return true;
                }
                break;

            case ColumnType.Uuid:
                if (field.ValueKind == JsonValueKind.String && Guid.TryParse(field.GetString(), out var guid))
                {
                    value = guid;
                    return true;
                }
                break;

            case ColumnType.Jsonb:
                value = field.GetRawText();
                return true;

            default:
                reason = $"unsupported column type '{column.Type}' for field '{column.Name}'";
                return false;
        }

        reason = $"field '{column.Name}' is not convertible to {column.Type}";
        return false;
    }
}
