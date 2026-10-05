using System.Text.Json;
using System.Text.Json.Serialization;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Schema;

/// <summary>
/// The stored declaration format is the one declarations already on disk were written in: the
/// compile-time metadata must write, byte for byte, what the reflection-based options the stores used
/// before wrote — or a store would read its own old declarations differently.
/// </summary>
public class DeclarationJsonTests
{
    // What the SQLite and PostgreSQL hint sources serialized with before the format moved here.
    private static readonly JsonSerializerOptions Before = new() { Converters = { new JsonStringEnumConverter() } };

    private static readonly IReadOnlyList<FieldHint> Fields =
    [
        new FieldHint("lot", ColumnType.Text, Nullable: false, SourceKey: "LOT"),
        new FieldHint("qty", ColumnType.Integer),
        new FieldHint("machine", ColumnType.Text, Binding: FieldBinding.Snapshot,
            Target: new EntityRef(FormTypeRef.Create("machine"), "name", lookupKey: "no", viaField: "machine_no")),
        new FieldHint("site", ColumnType.Text, Binding: FieldBinding.Reference, Target: new EntityRef(FormTypeRef.Create("site"), "name")),
    ];

    private static readonly IReadOnlyList<RelationHint> Relations =
    [
        new RelationHint("uses_machine", RelationKind.Reference, FormTypeRef.Create("machine"), "machine_no"),
        new RelationHint("lines", RelationKind.Child, FormTypeRef.Create("order_line"), "order_no"),
    ];

    [Fact]
    public void Fields_are_written_exactly_as_before()
    {
        DeclarationJson.SerializeFields(Fields).Should().Be(JsonSerializer.Serialize(Fields, Before));
    }

    [Fact]
    public void Relations_are_written_exactly_as_before()
    {
        DeclarationJson.SerializeRelations(Relations).Should().Be(JsonSerializer.Serialize(Relations, Before));
    }

    [Fact]
    public void What_was_written_before_reads_back_as_the_same_declaration()
    {
        DeclarationJson.DeserializeFields(JsonSerializer.Serialize(Fields, Before)).Should().Equal(Fields);
        DeclarationJson.DeserializeRelations(JsonSerializer.Serialize(Relations, Before)).Should().Equal(Relations);
        DeclarationJson.DeserializeRelations("null").Should().BeNull();
        DeclarationJson.DeserializeFields("null").Should().BeEmpty();
    }
}
