using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Schema;

/// <summary>
/// What a declaration says about a field's time binding (Formology 0.1 rules 6 and 7): a declared binding is what it says, a
/// field that takes its value from another form type and says nothing is undecided, every other silent field is its own value —
/// and a field that says nothing is materialized exactly as one that declared «stored», so a declaration that never said
/// changes no behavior.
/// </summary>
public class BindingStateTests
{
    private static readonly FormTypeRef Inspection = FormTypeRef.Create("inspection");
    private static readonly EntityRef Machine = new(FormTypeRef.Create("machine"), "name");

    private static FormTypeHints Declare(IReadOnlyList<FieldHint> fields, IReadOnlyList<RelationHint>? relations = null) =>
        new(Inspection, "inspection", fields, relations);

    [Theory]
    [InlineData(FieldBinding.Stored, BindingState.Stored)]
    [InlineData(FieldBinding.Snapshot, BindingState.Snapshot)]
    [InlineData(FieldBinding.Reference, BindingState.Reference)]
    public void A_declared_binding_is_what_it_says_even_with_a_target(FieldBinding declared, BindingState expected)
    {
        var field = new FieldHint("machine", ColumnType.Text, Binding: declared, Target: Machine);

        Declare([field]).BindingStateOf(field).Should().Be(expected);
    }

    [Fact]
    public void A_field_with_a_target_and_no_binding_is_undecided()
    {
        var field = new FieldHint("machine", ColumnType.Text, Target: Machine);

        Declare([field]).BindingStateOf(field).Should().Be(BindingState.Undecided);
    }

    [Fact]
    public void The_key_field_of_a_reference_relation_with_no_binding_is_undecided_and_that_of_a_child_relation_is_not()
    {
        var reference = new FieldHint("machine_no", ColumnType.Text);
        var childKey = new FieldHint("order_no", ColumnType.Text);
        var declaration = Declare(
            [reference, childKey],
            [
                new RelationHint("uses_machine", RelationKind.Reference, FormTypeRef.Create("machine"), "machine_no"),
                new RelationHint("lines", RelationKind.Child, FormTypeRef.Create("order_line"), "order_no"),
            ]);

        declaration.BindingStateOf(reference).Should().Be(BindingState.Undecided);
        declaration.BindingStateOf(childKey).Should().Be(BindingState.Stored, "a child relation's key field is the child's, not a value this form takes from another");
    }

    [Fact]
    public void A_field_that_points_nowhere_is_its_own_value()
    {
        var field = new FieldHint("lot", ColumnType.Text);

        Declare([field]).BindingStateOf(field).Should().Be(BindingState.Stored);
    }

    /// <summary>The difference the whole change keeps: silence and a decision to be the document's own value.</summary>
    [Fact]
    public void Silence_and_a_declared_stored_binding_stay_apart_through_the_stored_declaration()
    {
        var fields = new[]
        {
            new FieldHint("silent", ColumnType.Text, Target: Machine),
            new FieldHint("decided", ColumnType.Text, Binding: FieldBinding.Stored, Target: Machine),
        };

        var back = DeclarationJson.DeserializeFields(DeclarationJson.SerializeFields(fields));

        back[0].Binding.Should().BeNull();
        back[1].Binding.Should().Be(FieldBinding.Stored);
        back.Should().Equal(fields);
    }

    [Fact]
    public void A_declaration_stored_before_a_binding_could_be_left_out_reads_back_as_declared_stored()
    {
        const string stored = """[{"Name":"lot","Type":"Text","Nullable":true,"Binding":"Stored"}]""";

        DeclarationJson.DeserializeFields(stored).Should().ContainSingle().Which.Binding.Should().Be(FieldBinding.Stored);
    }

    [Fact]
    public async Task A_field_that_says_nothing_is_materialized_as_a_stored_value()
    {
        var hints = new Formbase.Core.InMemory.InMemoryFieldHintSource();
        hints.Declare(Declare([new FieldHint("machine", ColumnType.Text, Target: Machine)]));

        var schema = await new HintSchemaProposer(hints).ProposeAsync(Inspection, TestContext.Current.CancellationToken);

        schema!.Columns.Single(c => c.Name == "machine").Binding.Should().Be(FieldBinding.Stored);
    }
}
