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
/// <see cref="Lookup"/> and <see cref="ViaField"/> together answer <i>whose value</i>: how a record of
/// <see cref="Entity"/> is identified — by one of its fields, or by its record identity — and the field
/// of this declaration whose value identifies it. A snapshot of an equipment name taken by equipment
/// number is <c>(equipment, ValueField: name, Lookup: TargetLookup.Field("number"), ViaField: equipment_number)</c>.</para>
/// <para>The lookup pair is optional and comes whole: a declaration may say only where a value came
/// from, but a lookup key with no local field carrying it (or the reverse) names half a join.</para>
/// <para>Declaration writers refuse a column that does not exist (<see cref="DeclaredTargets"/>):
/// <see cref="ViaField"/> must be a field of the same declaration; <see cref="ValueField"/> and a
/// <see cref="TargetLookup.Field"/> lookup must be fields of <see cref="Entity"/> when it is declared. A target
/// declared later, or redeclared without the column, is not re-checked.</para>
/// </remarks>
[JsonConverter(typeof(EntityRefJsonConverter))]
public sealed record EntityRef
{
    /// <param name="entity">The form type the bound value comes from.</param>
    /// <param name="valueField">The field on <paramref name="entity"/> whose value the bound field carries.</param>
    /// <param name="lookup">How a record of <paramref name="entity"/> is identified; given with <paramref name="viaField"/> or not at all.</param>
    /// <param name="viaField">The field of this declaration carrying the identifying value; given with <paramref name="lookup"/> or not at all.</param>
    /// <exception cref="ArgumentException">A field name is blank, or only one of the lookup pair is given.</exception>
    public EntityRef(FormTypeRef entity, string valueField, TargetLookup? lookup = null, string? viaField = null)
    {
        if (string.IsNullOrWhiteSpace(valueField))
        {
            throw new ArgumentException($"A target on '{entity}' must name the field its value comes from.", nameof(valueField));
        }

        if (viaField is not null && string.IsNullOrWhiteSpace(viaField))
        {
            throw new ArgumentException("A via field, when given, must name a field.", nameof(viaField));
        }

        if ((lookup is null) != (viaField is null))
        {
            throw new ArgumentException(
                $"A target on '{entity}' names {(lookup is null ? "a via field but no lookup" : "a lookup but no via field")}. " +
                "The two are one join — how the target record is identified and the field here that carries the identifying value — so give both or neither.",
                lookup is null ? nameof(lookup) : nameof(viaField));
        }

        Entity = entity;
        ValueField = valueField;
        Lookup = lookup;
        ViaField = viaField;
    }

    /// <summary>The form type the bound value comes from.</summary>
    public FormTypeRef Entity { get; }

    /// <summary>
    /// The field on <see cref="Entity"/> whose value the bound field carries — the column a
    /// <c>snapshot</c> was copied from, or the column a <c>reference</c> reads. Not a lookup key.
    /// </summary>
    public string ValueField { get; }

    /// <summary>How the record the value belongs to is identified on <see cref="Entity"/>, when declared.</summary>
    public TargetLookup? Lookup { get; }

    /// <summary>The field of this declaration whose value identifies the record <see cref="Lookup"/> finds, when declared.</summary>
    public string? ViaField { get; }
}

/// <summary>
/// Stores an <see cref="EntityRef"/> by its member names, and reads the shape releases before 0.17.0
/// stored — <c>{"Entity": …, "KeyField": …}</c>, where <c>KeyField</c> already meant the value column —
/// so a declaration on disk keeps its target across the upgrade instead of reading back without one.
/// A lookup by field is stored as <c>"LookupKey": "&lt;field&gt;"</c> — the form releases before 0.20.0
/// wrote, so their declarations read back as <see cref="TargetLookup.Field"/> — and a lookup by
/// record as <c>"LookupRecord": true</c>.
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
        var lookupRecord = false;
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
                case "LookupKey":
                    lookupKey = reader.GetString();
                    break;
                case "LookupRecord":
                    lookupRecord = reader.GetBoolean();
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

        if (lookupRecord && lookupKey is not null)
        {
            throw new JsonException("A stored entity reference names both a lookup field and a lookup by record.");
        }

        var lookup = lookupRecord ? TargetLookup.Record : lookupKey is not null ? TargetLookup.Field(lookupKey) : null;
        return new EntityRef(entity.Value, valueField ?? legacyKeyField ?? throw new JsonException("A stored entity reference names no value field."), lookup, viaField);
    }

    public override void Write(Utf8JsonWriter writer, EntityRef value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName(nameof(EntityRef.Entity));
        EntityConverter.Write(writer, value.Entity, options);
        writer.WriteString(nameof(EntityRef.ValueField), value.ValueField);
        switch (value.Lookup)
        {
            case TargetLookup.FieldLookup field:
                writer.WriteString("LookupKey", field.Name);
                writer.WriteString(nameof(EntityRef.ViaField), value.ViaField);
                break;
            case TargetLookup.RecordLookup:
                writer.WriteBoolean("LookupRecord", true);
                writer.WriteString(nameof(EntityRef.ViaField), value.ViaField);
                break;
        }

        writer.WriteEndObject();
    }
}
