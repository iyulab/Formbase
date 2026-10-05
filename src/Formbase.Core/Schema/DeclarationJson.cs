using System.Text.Json;
using System.Text.Json.Serialization;

namespace Formbase.Core.Schema;

/// <summary>
/// The JSON a durable <see cref="Ports.IFieldHintSource"/> stores a declaration's fields and relations
/// in — one format for every store, kept where the declaration types and their converters live. Member
/// names as declared and enums by name, which is the shape declarations already on disk were written
/// in. The metadata is generated at compile time, so a store that uses this works in Native AOT and
/// trimmed hosts.
/// </summary>
public static class DeclarationJson
{
    /// <summary>The fields as stored.</summary>
    public static string SerializeFields(IReadOnlyList<FieldHint> fields) =>
        JsonSerializer.Serialize(fields, DeclarationJsonContext.Default.IReadOnlyListFieldHint);

    /// <summary>Stored fields read back; an empty list for a JSON <c>null</c>.</summary>
    public static IReadOnlyList<FieldHint> DeserializeFields(string json) =>
        JsonSerializer.Deserialize(json, DeclarationJsonContext.Default.ListFieldHint) ?? [];

    /// <summary>The relations as stored.</summary>
    public static string SerializeRelations(IReadOnlyList<RelationHint> relations) =>
        JsonSerializer.Serialize(relations, DeclarationJsonContext.Default.IReadOnlyListRelationHint);

    /// <summary>Stored relations read back; null for a JSON <c>null</c>.</summary>
    public static IReadOnlyList<RelationHint>? DeserializeRelations(string json) =>
        JsonSerializer.Deserialize(json, DeclarationJsonContext.Default.ListRelationHint);
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(List<FieldHint>))]
[JsonSerializable(typeof(List<RelationHint>))]
[JsonSerializable(typeof(IReadOnlyList<FieldHint>))]
[JsonSerializable(typeof(IReadOnlyList<RelationHint>))]
internal sealed partial class DeclarationJsonContext : JsonSerializerContext;
