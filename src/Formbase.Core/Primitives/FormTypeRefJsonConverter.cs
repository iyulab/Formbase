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
/// the empty default. No version since 0.14.1 writes that object form, and it is no longer read: it is
/// refused with the way to rewrite it, so a store that still holds one is told so instead of misreading it.
/// </remarks>
internal sealed class FormTypeRefJsonConverter : JsonConverter<FormTypeRef>
{
    public override FormTypeRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? value = reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.StartObject => throw new JsonException(
                "A form type is a string, not an object. The object form ({\"Value\":\"…\"}) was written only by versions before 0.14.1 "
                + "and is no longer read: put the declaration again with a release that still reads it (0.36.x), which stores the string form, then upgrade."),
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
}
