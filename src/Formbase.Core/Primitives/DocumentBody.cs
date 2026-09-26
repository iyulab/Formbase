using System.Text.Json;

namespace Formbase.Core.Primitives;

/// <summary>
/// The opaque content of a document. The engine stores it verbatim at intake and does not
/// interpret its structure there; only the projector reads fields out of it against a schema.
/// Backed by a detached <see cref="JsonElement"/> so it survives the source document's disposal.
/// <para>
/// An object that names one property twice is refused. JSON leaves its meaning open (RFC 8259 §4) and
/// I-JSON forbids it (RFC 7493 §2.3); stores disagree on it — PostgreSQL's <c>jsonb</c> keeps only the
/// last value — so accepting it would store one body and give back another, depending on the store.
/// </para>
/// </summary>
public sealed class DocumentBody
{
    /// <summary>The document content as a JSON value.</summary>
    public JsonElement Root { get; }

    private DocumentBody(JsonElement root) => Root = root;

    /// <summary>
    /// The options a body is parsed with: duplicate property names are refused while reading, so a
    /// caller parsing a body itself can refuse it the same way.
    /// </summary>
    public static JsonDocumentOptions ParseOptions { get; } = new() { AllowDuplicateProperties = false };

    /// <summary>Wraps a JSON element, detaching it from its owning document.</summary>
    /// <exception cref="ArgumentException">An object in <paramref name="element"/> names a property twice.</exception>
    public static DocumentBody From(JsonElement element)
    {
        if (FindDuplicate(element) is { } path)
        {
            throw new ArgumentException($"The document names the property at '{path}' more than once.", nameof(element));
        }

        return new DocumentBody(element.Clone());
    }

    /// <summary>Parses JSON text into a document body.</summary>
    /// <exception cref="JsonException">The text is not JSON, or an object in it names a property twice.</exception>
    public static DocumentBody Parse(string json)
    {
        using var document = JsonDocument.Parse(json, ParseOptions);
        return new DocumentBody(document.RootElement.Clone());
    }

    private static string? FindDuplicate(JsonElement element, string path = "$")
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in element.EnumerateObject())
                {
                    var at = $"{path}.{property.Name}";
                    if (!seen.Add(property.Name))
                    {
                        return at;
                    }

                    if (FindDuplicate(property.Value, at) is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            case JsonValueKind.Array:
                var index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    if (FindDuplicate(item, $"{path}[{index++}]") is { } nested)
                    {
                        return nested;
                    }
                }

                return null;

            default:
                return null;
        }
    }

    /// <summary>Serializes the body back to its raw JSON text.</summary>
    public string ToJsonString() => Root.GetRawText();
}
