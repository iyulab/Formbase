using System.Text.Json;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// Cuts a repeated section's rows out of a parent document (<see cref="SectionDef"/>): one row per item
/// of the section array, each carrying the parent's key value. The projector builds a section's table
/// from these, and any other reader that wants the section's rows rather than the parent's documents
/// uses the same cut, so the two cannot disagree about what a row is.
/// </summary>
public static class SectionRows
{
    /// <summary>
    /// The rows <paramref name="parent"/> holds for <paramref name="section"/>, or why it holds none that
    /// can be read. A parent with no section field, or a null one, holds no rows and is not refused — an
    /// order with no lines is an order.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="parent"/> is a retirement and has no content.</exception>
    public static SectionExpansion Expand(StoredDocument parent, SectionDef section)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(section);

        var root = (parent.Body ?? throw new ArgumentException("A retirement has no rows.", nameof(parent))).Root;
        if (root.ValueKind != JsonValueKind.Object)
        {
            return SectionExpansion.Refused($"the document is a JSON {Kind(root)}, not an object, so it holds no section '{section.SectionField}'");
        }

        if (!root.TryGetProperty(section.SectionField, out var items) || items.ValueKind == JsonValueKind.Null)
        {
            return SectionExpansion.None;
        }

        if (items.ValueKind != JsonValueKind.Array)
        {
            return SectionExpansion.Refused($"section '{section.SectionField}' is a JSON {Kind(items)}, not an array of rows");
        }

        var parentRecord = ProjectionSystemColumns.RecordOf(parent.Id, parent.Key);
        var key = section.ParentKey is null
            ? default(JsonElement?)
            : SourceKeys.TryResolve(root, section.ParentKey, out var value) ? value : null;

        var rows = new List<SectionRow>(items.GetArrayLength());
        var position = 0;
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                return SectionExpansion.Refused($"row {position} of section '{section.SectionField}' is a JSON {Kind(item)}, not an object");
            }

            rows.Add(new SectionRow(position, $"{parentRecord}/{position}", WithKey(item, section, key, parentRecord)));
            position++;
        }

        return new SectionExpansion(rows, null);
    }

    /// <summary>
    /// The item with the parent's key value written under <see cref="SectionDef.RowKey"/>, first. The
    /// parent's value wins over an item property of the same name: the key is the link, and a row that
    /// named another parent would join to the wrong one.
    /// </summary>
    private static JsonElement WithKey(JsonElement item, SectionDef section, JsonElement? parentKey, string parentRecord)
    {
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(section.RowKey);
            if (section.ParentKey is null)
            {
                writer.WriteStringValue(parentRecord);
            }
            else if (parentKey is { } value)
            {
                value.WriteTo(writer);
            }
            else
            {
                writer.WriteNullValue();
            }

            foreach (var property in item.EnumerateObject())
            {
                if (!string.Equals(property.Name, section.RowKey, StringComparison.Ordinal))
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return document.RootElement.Clone();
    }

    private static string Kind(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Array => "array",
        JsonValueKind.Object => "object",
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        _ => "null",
    };
}

/// <summary>The rows a parent document holds for a section, or why none can be read (<see cref="Refusal"/>).</summary>
/// <param name="Rows">The rows, in array order; empty when refused or when the document holds none.</param>
/// <param name="Refusal">Why the document's section cannot be read, or null when it can.</param>
public sealed record SectionExpansion(IReadOnlyList<SectionRow> Rows, string? Refusal)
{
    internal static SectionExpansion None { get; } = new([], null);

    internal static SectionExpansion Refused(string reason) => new([], reason);
}

/// <summary>One row of a repeated section.</summary>
/// <param name="Position">The item's position in the section array, from 0.</param>
/// <param name="Record">
/// The row's record identity: the parent's (<see cref="ProjectionSystemColumns.RecordOf"/>), a slash, and
/// the position. A correction of the parent replaces every row it held, so the position is stable for as
/// long as the parent document it was cut from stands.
/// </param>
/// <param name="Body">The item with the parent's key value written under <see cref="SectionDef.RowKey"/>.</param>
public sealed record SectionRow(int Position, string Record, JsonElement Body);
