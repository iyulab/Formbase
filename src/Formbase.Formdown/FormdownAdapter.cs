using System.Globalization;
using System.Text;
using System.Text.Json;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Formdown;

/// <summary>
/// Translates formdown into what the engine accepts: a form's schema — the JSON formdown core's
/// <c>getSchema()</c> returns — into <see cref="FormTypeHints"/>, and a filled form's data into a typed
/// <see cref="DocumentBody"/>. The schema is read as JSON on purpose: formdown has one parser, and this
/// adapter does not become a second one.
/// </summary>
/// <remarks>
/// <para>A label someone wrote becomes the field's <see cref="FieldHint.BusinessName"/>. The translation is
/// lossy and says so: everything the hint vocabulary has no slot for — an option list, a range, a
/// pattern, a formula — is reported as a <see cref="VocabularyGap"/>
/// instead of being dropped silently.</para>
/// <para>formdown hands values over as text (an input's value is a string, a computed field holds its
/// number as text), while a projected <see cref="ColumnType.Integer"/> or <see cref="ColumnType.Decimal"/>
/// column takes a JSON number. <see cref="AdaptData"/> is the step that closes that distance, so a filled
/// form is stored with the types its schema declares.</para>
/// </remarks>
public static class FormdownAdapter
{
    /// <summary>The result of adapting one formdown schema: the hints, and what did not fit them.</summary>
    /// <param name="Hints">The declaration for the form type, one field per data field of the form, in form order.</param>
    /// <param name="Gaps">The constructs the hints cannot carry.</param>
    public sealed record SchemaResult(FormTypeHints Hints, IReadOnlyList<VocabularyGap> Gaps);

    /// <summary>
    /// Adapts a formdown schema — the JSON of <c>getSchema(content)</c>, an object of field name to field
    /// schema — into the declaration for <paramref name="type"/>.
    /// </summary>
    /// <param name="schemaJson">The schema as formdown core returns it.</param>
    /// <param name="type">The form type the declaration is for; a formdown schema does not name its form.</param>
    /// <param name="tableName">The table the form type projects into.</param>
    /// <exception cref="FormatException">The JSON is not a formdown schema (not an object of field objects with a <c>type</c>).</exception>
    public static SchemaResult AdaptSchema(string schemaJson, FormTypeRef type, string tableName)
    {
        ArgumentNullException.ThrowIfNull(schemaJson);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        using var document = ParseObject(schemaJson, "A formdown schema");
        var declared = new List<(string Name, JsonElement Schema, int Order)>();
        var order = 0;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"A formdown schema maps each field name to an object; '{property.Name}' maps to {property.Value.ValueKind}.");
            }

            declared.Add((property.Name, property.Value.Clone(), order++));
        }

        var fields = new List<FieldHint>();
        var gaps = new List<VocabularyGap>();
        var declaration = type.Value;
        foreach (var (name, schema, _) in declared
            .OrderBy(f => Position(f.Schema) ?? int.MaxValue)
            .ThenBy(f => f.Order))
        {
            var field = AdaptField(declaration, name, schema, gaps);
            if (field is not null)
            {
                fields.Add(field);
            }
        }

        return new SchemaResult(new FormTypeHints(type, tableName, fields), gaps);
    }

    /// <summary>
    /// Adapts a filled formdown form — the data object formdown hands over (field name to a string, a list
    /// of strings, or a boolean) — into a document typed by <paramref name="hints"/>: a number field's text
    /// becomes a JSON number, and an empty number or date field becomes null.
    /// </summary>
    /// <remarks>
    /// Only what can be read without guessing is changed. A text that is not a number in the invariant
    /// form (<c>1234.5</c>, not <c>1,234.5</c>) is kept as it came, so the projection reports the field as
    /// not convertible instead of storing a value someone misread. Fields the hints do not declare pass
    /// through unchanged — the raw document keeps everything the form sent.
    /// </remarks>
    /// <param name="formDataJson">The form's data as JSON.</param>
    /// <param name="hints">The declaration the data is typed by — normally the one <see cref="AdaptSchema"/> made from the same form.</param>
    /// <exception cref="FormatException">The JSON is not an object.</exception>
    public static DocumentBody AdaptData(string formDataJson, FormTypeHints hints)
    {
        ArgumentNullException.ThrowIfNull(formDataJson);
        ArgumentNullException.ThrowIfNull(hints);

        using var document = ParseObject(formDataJson, "Form data");
        var types = new Dictionary<string, ColumnType>(StringComparer.Ordinal);
        foreach (var field in hints.Fields)
        {
            types.TryAdd(field.SourceKey ?? field.Name, field.Type);
        }

        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (types.TryGetValue(property.Name, out var columnType))
                {
                    WriteTyped(writer, property.Value, columnType);
                }
                else
                {
                    property.Value.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        return DocumentBody.Parse(Encoding.UTF8.GetString(buffer.ToArray()));
    }

    private static FieldHint? AdaptField(string declaration, string name, JsonElement schema, List<VocabularyGap> gaps)
    {
        if (!schema.TryGetProperty("type", out var typeElement) || typeElement.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Field '{name}' of a formdown schema has no type.");
        }

        var formdownType = typeElement.GetString()!;
        if (formdownType is "submit" or "reset")
        {
            return null; // A button, not a value the form carries.
        }

        var validation = schema.TryGetProperty("validation", out var v) && v.ValueKind == JsonValueKind.Object ? v : default;
        var hasOptions = schema.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array && options.GetArrayLength() > 0;
        var columnType = MapType(declaration, name, formdownType, hasOptions, validation, gaps);
        var nullable = !(schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.True);

        // formdown fills a missing label from the name (snake_case and camelCase become capitalized
        // words); only a label someone wrote is a name a person reads the field by.
        string? businessName = null;
        if (schema.TryGetProperty("label", out var label) && label.ValueKind == JsonValueKind.String)
        {
            var text = label.GetString();
            if (!string.IsNullOrWhiteSpace(text) && text != DefaultLabel(name))
            {
                businessName = text.Trim();
            }
        }

        if (hasOptions)
        {
            var allowOther = schema.TryGetProperty("allowOther", out var other) && other.ValueKind == JsonValueKind.True;
            gaps.Add(new VocabularyGap(declaration, name, VocabularyGapKind.Constraint,
                $"options [{string.Join(", ", options.EnumerateArray().Select(FormatOption))}]{(allowOther ? " + other" : string.Empty)}",
                allowOther
                    ? "The value set has no slot, and with 'other' allowed it is open anyway; the column takes any text."
                    : "The value set has no slot; the column takes any text, not only the listed values."));
        }

        if (validation.ValueKind == JsonValueKind.Object)
        {
            foreach (var rule in validation.EnumerateObject())
            {
                gaps.Add(new VocabularyGap(declaration, name, VocabularyGapKind.Constraint,
                    $"{rule.Name}={FormatScalar(rule.Value)}",
                    "A declared value rule has no hint slot; the form checks it when it is filled, the projection does not."));
            }
        }

        if (schema.TryGetProperty("compute", out var compute) && compute.ValueKind == JsonValueKind.String)
        {
            // formdown works the value out while the form is filled and hands it over with the other
            // values, so the document carries it and it is declared like any field; only the formula drops.
            gaps.Add(new VocabularyGap(declaration, name, VocabularyGapKind.Derived,
                $"compute \"{compute.GetString()}\"",
                "The formula has no slot; the value the form worked out is stored, and is not recomputed when the fields it reads are corrected."));
        }

        return new FieldHint(name, columnType, nullable, BusinessName: businessName);
    }

    private static ColumnType MapType(string declaration, string name, string formdownType, bool hasOptions, JsonElement validation, List<VocabularyGap> gaps)
    {
        switch (formdownType)
        {
            case "text" or "email" or "password" or "tel" or "url" or "textarea" or "color" or "select" or "radio":
                return ColumnType.Text;
            case "number" or "range":
                return IsWholeNumber(validation) ? ColumnType.Integer : ColumnType.Decimal;
            case "checkbox" when hasOptions:
                // A checkbox group hands over the list of the values checked.
                gaps.Add(new VocabularyGap(declaration, name, VocabularyGapKind.Unresolved,
                    "checkbox group",
                    "Several checked values are one JSONB array; the element type (one of the options) drops."));
                return ColumnType.Jsonb;
            case "checkbox" or "toggle":
                return ColumnType.Boolean;
            case "date" or "datetime-local":
                return ColumnType.Timestamp;
            case "time" or "month" or "week":
                gaps.Add(new VocabularyGap(declaration, name, VocabularyGapKind.Unresolved,
                    $"type {formdownType}",
                    $"A {formdownType} is not a point in time; it is stored as the text the form hands over."));
                return ColumnType.Text;
            case "file":
                gaps.Add(new VocabularyGap(declaration, name, VocabularyGapKind.Unresolved,
                    "type file",
                    "File content is not a document value; the column holds whatever text the application writes for the file (a name or a reference)."));
                return ColumnType.Text;
            default:
                gaps.Add(new VocabularyGap(declaration, name, VocabularyGapKind.Unresolved,
                    $"type {formdownType}", "Unknown formdown field type; degraded to Text."));
                return ColumnType.Text;
        }
    }

    /// <summary>
    /// A number input takes whole numbers unless its step says otherwise: with no step the step is 1,
    /// counted from <c>min</c> (HTML <c>input type=number</c>), so a whole step from a whole (or absent)
    /// minimum admits only whole numbers. A fractional step, <c>step="any"</c>, or a fractional minimum
    /// admits fractions.
    /// </summary>
    private static bool IsWholeNumber(JsonElement validation)
    {
        if (validation.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        if (validation.TryGetProperty("step", out var step) && !IsWhole(step))
        {
            return false;
        }

        return !validation.TryGetProperty("min", out var min) || IsWhole(min);
    }

    private static bool IsWhole(JsonElement value) =>
        value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetDecimal(out var d) && d == decimal.Truncate(d),
            JsonValueKind.String => decimal.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d == decimal.Truncate(d),
            _ => false,
        };

    private static void WriteTyped(Utf8JsonWriter writer, JsonElement value, ColumnType type)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString()!;
            switch (type)
            {
                case ColumnType.Integer or ColumnType.Decimal or ColumnType.Timestamp when text.Trim().Length == 0:
                    // An input left empty: no value, not an empty one.
                    writer.WriteNullValue();
                    return;
                case ColumnType.Integer when long.TryParse(text.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var whole):
                    writer.WriteNumberValue(whole);
                    return;
                case ColumnType.Decimal when decimal.TryParse(text.Trim(), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var number):
                    writer.WriteNumberValue(number);
                    return;
            }
        }

        value.WriteTo(writer);
    }

    private static int? Position(JsonElement schema) =>
        schema.TryGetProperty("position", out var position) && position.ValueKind == JsonValueKind.Number && position.TryGetInt32(out var p) ? p : null;

    private static string FormatOption(JsonElement option)
    {
        if (option.ValueKind == JsonValueKind.Object)
        {
            var value = option.TryGetProperty("value", out var v) ? FormatScalar(v) : "?";
            return option.TryGetProperty("label", out var l) && l.ValueKind == JsonValueKind.String && l.GetString() != value
                ? $"{value}={l.GetString()}"
                : value;
        }

        return FormatScalar(option);
    }

    private static string FormatScalar(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText();

    /// <summary>The label formdown gives a field that declares none (its parser's <c>formatLabel</c>).</summary>
    internal static string DefaultLabel(string name)
    {
        IEnumerable<string> words;
        if (name.Contains('_', StringComparison.Ordinal))
        {
            words = name.Split('_');
        }
        else if (HasLowerUpper(name))
        {
            var spaced = new StringBuilder(name.Length + 4);
            for (var i = 0; i < name.Length; i++)
            {
                if (i > 0 && char.IsAsciiLetterLower(name[i - 1]) && char.IsAsciiLetterUpper(name[i]))
                {
                    spaced.Append(' ');
                }

                spaced.Append(name[i]);
            }

            words = spaced.ToString().Split(' ');
        }
        else
        {
            words = [name];
        }

        return string.Join(' ', words.Select(Capitalize));
    }

    private static bool HasLowerUpper(string name)
    {
        for (var i = 1; i < name.Length; i++)
        {
            if (char.IsAsciiLetterLower(name[i - 1]) && char.IsAsciiLetterUpper(name[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string Capitalize(string word) =>
        word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..].ToLowerInvariant();

    private static JsonDocument ParseObject(string json, string what)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException($"{what} is not valid JSON: {ex.Message}", ex);
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            var kind = document.RootElement.ValueKind;
            document.Dispose();
            throw new FormatException($"{what} must be a JSON object, not {kind}.");
        }

        return document;
    }
}
