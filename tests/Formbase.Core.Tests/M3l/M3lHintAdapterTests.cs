using Formbase.Core.Schema;
using Formbase.M3L;

namespace Formbase.Core.Tests.M3l;

/// <summary>
/// The adapter is a measurement instrument: these tests pin both directions — what the flat
/// vocabulary carries (hints) and what it provably drops (gaps). Every gap assertion here is a
/// unit of measured demand for the §10 vocabulary design.
/// </summary>
public class M3lHintAdapterTests
{
    private const string InspectionForm =
        """
        ## Inspection

        - id: identifier @pk @generated
        - lot_no(로트 번호): string(50)
        - qty: integer @min(0)
        - passed: boolean
        - inspected_at: timestamp
        - inspector_id: identifier @reference(Employee)
        - status: enum = "pass"
          - pass: "합격"
          - fail: "불합격"
        - unit: string? # MasterItem.Key
        - approved_price: decimal(10,2) # PriceBook.Amount!

        ### Lookup
        - inspector_name: string @lookup(inspector_id.name)

        ## InspectionDefect

        - id: identifier @pk @generated
        - inspection_id: identifier @reference(Inspection)
        - defect_code: string(20)
        """;

    [Fact]
    public void Models_map_to_one_flat_hint_set_each()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Hints.Should().HaveCount(2);
        var inspection = result.Hints[0];
        inspection.Type.Value.Should().Be("inspection");
        inspection.TableName.Should().Be("inspection");
        result.Hints[1].TableName.Should().Be("inspection_defect");
    }

    [Fact]
    public void Stored_fields_survive_with_mapped_types()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        var fields = result.Hints[0].Fields.ToDictionary(f => f.Name);
        fields["id"].Type.Should().Be(ColumnType.Uuid);
        fields["lot_no"].Type.Should().Be(ColumnType.Text);
        fields["qty"].Type.Should().Be(ColumnType.Integer);
        fields["passed"].Type.Should().Be(ColumnType.Boolean);
        fields["inspected_at"].Type.Should().Be(ColumnType.Timestamp);
        fields["approved_price"].Type.Should().Be(ColumnType.Decimal);
        fields["unit"].Nullable.Should().BeTrue("the '?' suffix is the one nullability signal the vocabulary keeps");
    }

    [Fact]
    public void The_reference_target_drops_and_is_measured()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        // The raw key column survives; the entity it points at does not — Formology rule 4.
        result.Hints[0].Fields.Should().Contain(f => f.Name == "inspector_id");
        result.Gaps.Should().Contain(g =>
            g.Model == "Inspection" && g.Field == "inspector_id" && g.Kind == VocabularyGapKind.Relation);
        result.Gaps.Should().Contain(g =>
            g.Model == "InspectionDefect" && g.Field == "inspection_id" && g.Kind == VocabularyGapKind.Relation);
    }

    [Fact]
    public void The_display_label_drops_and_is_measured()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Gaps.Should().Contain(g =>
            g.Field == "lot_no" && g.Kind == VocabularyGapKind.IdentityDisplay && g.Construct.Contains("로트 번호"));
    }

    [Fact]
    public void Derived_fields_are_excluded_from_hints_and_measured()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Hints[0].Fields.Should().NotContain(f => f.Name == "inspector_name",
            "a lookup is not a raw extraction key");
        result.Gaps.Should().Contain(g =>
            g.Field == "inspector_name" && g.Kind == VocabularyGapKind.Derived);
    }

    [Fact]
    public void Binding_modes_are_measured_as_the_time_axis()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Gaps.Should().Contain(g =>
            g.Field == "unit" && g.Kind == VocabularyGapKind.TimeBinding && g.Construct.Contains("soft"));
        result.Gaps.Should().Contain(g =>
            g.Field == "approved_price" && g.Kind == VocabularyGapKind.TimeBinding && g.Construct.Contains("hard"));
    }

    [Fact]
    public void Enum_membership_degrades_to_text_and_is_measured()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Hints[0].Fields.Single(f => f.Name == "status").Type.Should().Be(ColumnType.Text);
        result.Gaps.Should().Contain(g =>
            g.Field == "status" && g.Kind == VocabularyGapKind.Constraint && g.Construct == "inline enum");
    }
}
