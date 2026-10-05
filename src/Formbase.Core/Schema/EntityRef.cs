using System.Text.Json;
using System.Text.Json.Serialization;
using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// What a bound field points at on another form type: the column its value comes from and, when the
/// declaration says so, how to find the record that value belongs to. Rendered to a physical
/// <c>table.column</c> string (from <see cref="ValueField"/>) when a proposal materializes.
/// </summary>
/// <remarks>
/// <para>Three columns, three questions. <see cref="ValueField"/> answers <i>which value</i> — the
/// column a <c>snapshot</c> was copied from, or the column a <c>reference</c> reads.
/// <see cref="LookupKey"/> and <see cref="ViaField"/> together answer <i>whose value</i>: the column
/// on <see cref="Entity"/> that identifies a record, and the field of this declaration whose value is
/// that record's key. A snapshot of an equipment name taken by equipment number is
/// <c>(equipment, ValueField: name, LookupKey: number, ViaField: equipment_number)</c>.</para>
/// <para>The lookup pair is optional and comes whole: a declaration may say only where a value came
/// from, but a lookup key with no local field carrying it (or the reverse) names half a join.</para>
/// <para>Declaration writers refuse a column that does not exist (<see cref="DeclaredTargets"/>):
/// <see cref="ViaField"/> must be a field of the same declaration; <see cref="ValueField"/> and
/// <see cref="LookupKey"/> must be fields of <see cref="Entity"/> when it is declared. A target
/// declared later, or redeclared without the column, is not re-checked.</para>
/// </remarks>
[JsonConverter(typeof(EntityRefJsonConverter))]
public sealed record EntityRef
{
    /// <param name="entity">The form type the bound value comes from.</param>
    /// <param name="valueField">The field on <paramref name="entity"/> whose value the bound field carries.</param>
    /// <param name="lookupKey">The field on <paramref name="entity"/> that identifies a record; given with <paramref name="viaField"/> or not at all.</param>
    /// <param name="viaField">The field of this declaration carrying the lookup key's value; given with <paramref name="lookupKey"/> or not at all.</param>
    /// <exception cref="ArgumentException">A field name is blank, or only one of the lookup pair is given.</exception>
    public EntityRef(FormTypeRef entity, string valueField, string? lookupKey = null, string? viaField = null)
    {
        if (string.IsNullOrWhiteSpace(valueField))
        {
            throw new ArgumentException($"A target on '{entity}' must name the field its value comes from.", nameof(valueField));
        }

        if (lookupKey is not null && string.IsNullOrWhiteSpace(lookupKey))
        {
            throw new ArgumentException("A lookup key, when given, must name a field.", nameof(lookupKey));
        }

        if (viaField is not null && string.IsNullOrWhiteSpace(viaField))
        {
            throw new ArgumentException("A via field, when given, must name a field.", nameof(viaField));
        }

        if ((lookupKey is null) != (viaField is null))
        {
            throw new ArgumentException(
                $"A target on '{entity}' names {(lookupKey is null ? "a via field but no lookup key" : "a lookup key but no via field")}. " +
                "The two are one join — the key column on the target and the field here that carries its value — so give both or neither.",
                lookupKey is null ? nameof(lookupKey) : nameof(viaField));
        }

        Entity = entity;
        ValueField = valueField;
        LookupKey = lookupKey;
        ViaField = viaField;
    }

    /// <summary>The form type the bound value comes from.</summary>
    public FormTypeRef Entity { get; }

    /// <summary>
    /// The field on <see cref="Entity"/> whose value the bound field carries — the column a
    /// <c>snapshot</c> was copied from, or the column a <c>reference</c> reads. Not a lookup key.
    /// </summary>
    public string ValueField { get; }

    /// <summary>The field on <see cref="Entity"/> that identifies the record the value belongs to, when declared.</summary>
    public string? LookupKey { get; }

    /// <summary>The field of this declaration whose value is <see cref="LookupKey"/>'s, when declared.</summary>
    public string? ViaField { get; }
}

/// <summary>
/// Stores an <see cref="EntityRef"/> by its member names, and reads the shape releases before 0.17.0
/// stored — <c>{"Entity": …, "KeyField": …}</c>, where <c>KeyField</c> already meant the value column —
/// so a declaration on disk keeps its target across the upgrade instead of reading back without one.
/// </summary>
internal sealed class EntityRefJsonConverter : JsonConverter<EntityRef>
{
    // The target's own converter, called directly: going back through JsonSerializer with the
    // caller's options would need reflection metadata for FormTypeRef, which a Native AOT or trimmed
    // host does not have.
    private static readonly FormTypeRefJsonConverter EntityConverter = new();

    public override EntityRef Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("An entity reference is stored as an object.");
        }

        FormTypeRef? entity = null;
        string? valueField = null, legacyKeyField = null, lookupKey = null, viaField = null;
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case nameof(EntityRef.Entity):
                    entity = EntityConverter.Read(ref reader, typeof(FormTypeRef), options);
                    break;
                case nameof(EntityRef.ValueField):
                    valueField = reader.GetString();
                    break;
                case "KeyField":
                    legacyKeyField = reader.GetString();
                    break;
                case nameof(EntityRef.LookupKey):
                    lookupKey = reader.GetString();
                    break;
                case nameof(EntityRef.ViaField):
                    viaField = reader.GetString();
                    break;
                default:
                    reader.Skip();
                    break;
            }
        }

        if (entity is null)
        {
            throw new JsonException("A stored entity reference names no form type.");
        }

        return new EntityRef(entity.Value, valueField ?? legacyKeyField ?? throw new JsonException("A stored entity reference names no value field."), lookupKey, viaField);
    }

    public override void Write(Utf8JsonWriter writer, EntityRef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(EntityRef.Entity));
        EntityConverter.Write(writer, value.Entity, options);
        writer.WriteString(nameof(EntityRef.ValueField), value.ValueField);
        if (value.LookupKey is not null)
        {
            writer.WriteString(nameof(EntityRef.LookupKey), value.LookupKey);
            writer.WriteString(nameof(EntityRef.ViaField), value.ViaField);
        }

        writer.WriteEndObject();
    }
}
