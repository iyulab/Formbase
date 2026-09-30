using System.Text.Json;
using System.Text.Json.Serialization;

namespace Formbase.Core.Primitives;

/// <summary>
/// Writes a <see cref="FormTypeRef"/> as its identifier string and reads it back through
/// <see cref="FormTypeRef.Create"/>, so a form type stored as JSON comes back as the same form type.
/// </summary>
/// <remarks>
/// Without it the serializer wrote <c>{"Value":"…"}</c> — the getter is public — but could not read it
/// back: the constructor is private and the property has no setter, so every form type deserialized as
/// the empty default. That object form is still read, because declarations written that way are on disk.
/// </remarks>
internal sealed class FormTypeRefJsonConverter : JsonConverter<FormTypeRef>
{
    public override FormTypeRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.StartObject => ReadLegacyObject(ref reader),
            _ => throw new JsonException($"A form type is a string, not {reader.TokenType}."),
        };

        try
        {
            return FormTypeRef.Create(value!);
        }
        catch (ArgumentException ex)
        {
            throw new JsonException("A form type must be a non-empty identifier.", ex);
        }
    }

    public override void Write(Utf8JsonWriter writer, FormTypeRef value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Value);

    private static string? ReadLegacyObject(ref Utf8JsonReader reader)
    {
        string? value = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                throw new JsonException("A form type object holds only property names and values.");
            }

            var name = reader.GetString();
            reader.Read();
            if (string.Equals(name, nameof(FormTypeRef.Value), StringComparison.OrdinalIgnoreCase) && reader.TokenType == JsonTokenType.String)
            {
                value = reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        return value;
    }
}
