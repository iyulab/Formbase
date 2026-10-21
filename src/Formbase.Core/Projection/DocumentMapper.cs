using System.Text.Json;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// Maps a stored document into a projected row against a given domain schema. This is deterministic
/// value coercion, not type inference: the schema is already known (from hints), and each field is
/// read and converted per its declared column type. A required field that is missing or unconvertible
/// makes the whole document unmappable — reported as a skip, never a raw-store mutation. An optional
/// field that is present but unconvertible empties that one box and is reported as a field skip; the
/// row still lands.
/// </summary>
internal static class DocumentMapper
{
    public static bool TryMap(
        StoredDocument document,
        IReadOnlyList<ColumnDef> domainColumns,
        out IReadOnlyDictionary<string, object?> row,
        out IReadOnlyList<string> absentFields,
        out IReadOnlyList<ProjectionFieldSkip> fieldSkips,
        out string reason) =>
        // Only a standing record reaches here (RecordFold drops retirements), so a body is always present.
        TryMap(
            document,
            (document.Body ?? throw new ArgumentException("A retirement has no row to map.", nameof(document))).Root,
            ProjectionSystemColumns.RecordOf(document.Id, document.Key),
            sectionRow: null,
            domainColumns,
            out row,
            out absentFields,
            out fieldSkips,
            out reason);

    /// <summary>
    /// Maps <paramref name="root"/> — a document's body, or a row cut out of it (<see cref="SectionRows"/>)
    /// — as a row with <paramref name="record"/> as its record identity and, for a section row,
    /// <paramref name="sectionRow"/> as its position. The other system columns are the document's: a
    /// section row traces back to the parent document it was cut from, and shares its record key, so
    /// replacing the parent's record replaces every row it held.
    /// </summary>
    public static bool TryMap(
        StoredDocument document,
        JsonElement root,
        string record,
        int? sectionRow,
        IReadOnlyList<ColumnDef> domainColumns,
        out IReadOnlyDictionary<string, object?> row,
        out IReadOnlyList<string> absentFields,
        out IReadOnlyList<ProjectionFieldSkip> fieldSkips,
        out string reason)
    {
        var mapped = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            [ProjectionSystemColumns.DocumentId] = document.Id.Value,
            [ProjectionSystemColumns.Watermark] = document.Watermark.Value,
            [ProjectionSystemColumns.RecordKey] = document.Key?.Value,
            [ProjectionSystemColumns.Record] = record,
        };
        if (sectionRow is { } position)
        {
            mapped[ProjectionSystemColumns.Row] = (long)position;
        }

        List<string>? absent = null;
        List<ProjectionFieldSkip>? emptied = null;
        foreach (var column in domainColumns)
        {
            if (!TryConvert(root, column, out var value, out var fieldAbsent, out var unconvertible, out reason))
            {
                row = mapped;
                absentFields = [];
                fieldSkips = [];
                return false;
            }

            if (unconvertible is not null)
            {
                (emptied ??= []).Add(new ProjectionFieldSkip(document.Id, column.Name, unconvertible, document.Key));
            }

            if (fieldAbsent)
            {
                (absent ??= []).Add(column.Name);
            }

            mapped[column.Name] = value;
        }

        row = mapped;
        absentFields = absent ?? (IReadOnlyList<string>)[];
        fieldSkips = emptied ?? (IReadOnlyList<ProjectionFieldSkip>)[];
        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Converts one field. Returns false only when the document cannot be mapped at all; a present
    /// value an optional column cannot hold returns true with a null value and
    /// <paramref name="unconvertible"/> set to why.
    /// </summary>
    private static bool TryConvert(
        JsonElement root,
        ColumnDef column,
        out object? value,
        out bool absent,
        out string? unconvertible,
        out string reason)
    {
        unconvertible = null;
        if (TryConvertValue(root, column, out value, out absent, out reason))
        {
            return true;
        }

        // An optional column only fails here on a value that was there and could not be held (absent
        // or null already succeeded as null), so that is the case softened. A required column keeps
        // failing the document — the row cannot stand without it.
        if (!column.Nullable)
        {
            return false;
        }

        unconvertible = reason;
        reason = string.Empty;
        value = null;
        return true;
    }

    private static bool TryConvertValue(JsonElement root, ColumnDef column, out object? value, out bool absent, out string reason)
    {
        value = null;
        reason = string.Empty;

        if (column.Binding == FieldBinding.Reference)
        {
            // A reference reads true *now* — the target's current value — which this stage cannot
            // evaluate. The document's own copy is fixed-then, so answering with it would be a
            // wrong value wearing a plausible face; the box stays empty instead and the projection
            // result names the column. Absence counts stay out of it: this box was never the
            // document's to fill, so "the document never carried it" is not the fact being recorded.
            absent = false;
            return true;
        }

        // Read from the extraction key (identity), not the projected column name (display): a
        // renamed field keeps reading its original raw key, so its data follows the rename.
        var present = SourceKeys.TryResolve(root, column.ExtractionKey, out var field);
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

        if (column.Derivation is { } derivation)
        {
            if (derivation.TryDerive(field, out value, out var why))
            {
                return true;
            }

            reason = $"field '{column.Name}' cannot be computed ({derivation.Kind}): {why}";
            return false;
        }

        switch (column.Type)
        {
            case ColumnType.Text:
                // A scalar has a text form; an array or an object does not. Writing the structure's
                // JSON into a text column produced a plausible value ('["ITA"]') where every other
                // column type records a skip for the same input -- a wrong value is never found, an
                // empty one can be filled. A caller who wants the structure kept declares Jsonb. An
                // array may instead be a repeated section — rows of their own — which a child relation
                // names (RelationHint.SectionField) rather than a column.
                if (field.ValueKind == JsonValueKind.Array)
                {
                    reason = $"field '{column.Name}' is a JSON array, not a scalar, and cannot be projected as Text; declare the column as Jsonb to keep the array as one value, or, if its items are rows of their own (a repeated section), remove the column and name the array as the section field of a child relation — each item is then a row of the child";
                    return false;
                }

                if (field.ValueKind == JsonValueKind.Object)
                {
                    reason = $"field '{column.Name}' is a JSON object, not a scalar, and cannot be projected as Text; declare the column as Jsonb to keep the structure";
                    return false;
                }

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
                    && TimestampText.TryParse(field.GetString(), out var ts))
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
