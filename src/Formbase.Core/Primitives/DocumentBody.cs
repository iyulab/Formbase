using System.Text.Json;

namespace Formbase.Core.Primitives;

/// <summary>
/// The opaque content of a document. The engine stores it verbatim at intake and does not
/// interpret its structure there; only the projector reads fields out of it against a schema.
/// Backed by a detached <see cref="JsonElement"/> so it survives the source document's disposal.
/// </summary>
public sealed class DocumentBody
{
    /// <summary>The document content as a JSON value.</summary>
    public JsonElement Root { get; }

    private DocumentBody(JsonElement root) => Root = root;

    /// <summary>Wraps a JSON element, detaching it from its owning document.</summary>
    public static DocumentBody From(JsonElement element) => new(element.Clone());

    /// <summary>Parses JSON text into a document body.</summary>
    public static DocumentBody Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new DocumentBody(document.RootElement.Clone());
    }

    /// <summary>Serializes the body back to its raw JSON text.</summary>
    public string ToJsonString() => Root.GetRawText();
}
