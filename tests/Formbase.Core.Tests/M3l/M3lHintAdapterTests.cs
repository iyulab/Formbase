using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.M3L;
using Microsoft.Extensions.DependencyInjection;

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

    private const string FormWithRelationsSection =
        """
        ## Inspection

        - id: identifier @pk @generated
        - inspector_id: identifier @reference(Employee)

        ### Relations
        - >Employee: many-to-one
        - <InspectionDefect: one-to-many
        - <>Tag

        ## Employee

        - id: identifier @pk
        """;

    // The free-form Relations section is a construct the adapter does not decode: each entry is
    // recorded as an unresolved gap that keeps the source line, so the measured demand stays
    // countable and readable whatever shape the parser gives the entry. Field-level @reference is
    // the structured path and is filled, not counted here.
    [Fact]
    public void Each_relations_section_entry_is_an_unresolved_gap_that_keeps_its_source_line()
    {
        var result = M3lHintAdapter.Adapt(FormWithRelationsSection);

        var relationGaps = result.Gaps
            .Where(g => g.Model == "Inspection" && g.Construct.StartsWith("relations section:", StringComparison.Ordinal))
            .ToList();
        relationGaps.Should().HaveCount(3, "one gap per entry of the section");
        relationGaps.Should().OnlyContain(g => g.Kind == VocabularyGapKind.Unresolved && g.Field == null);
        relationGaps.Select(g => g.Construct).Should().SatisfyRespectively(
            c => c.Should().Contain(">Employee: many-to-one"),
            c => c.Should().Contain("<InspectionDefect: one-to-many"),
            c => c.Should().Contain("<>Tag"));

        // The entries do not leak into the hint as fields, and the structured reference still fills.
        result.Hints[0].Fields.Select(f => f.Name).Should().BeEquivalentTo(["id", "inspector_id"]);
        result.Hints[0].Relations.Should().ContainSingle(r => r.Name == "inspector_id");
    }

    [Fact]
    public void The_reference_relation_is_filled_not_dropped()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        // The raw key column survives; the entity it points at does not — Formology rule 4.
        // The vocabulary now carries the relation: the FK column survives AND the link is declared.
        result.Hints[0].Fields.Should().Contain(f => f.Name == "inspector_id");
        result.Hints[0].Relations.Should().ContainSingle(r =>
            r.Name == "inspector_id" && r.Kind == RelationKind.Reference
            && r.Target.Value == "employee" && r.KeyField == "inspector_id");
        result.Gaps.Should().NotContain(g => g.Kind == VocabularyGapKind.Relation,
            "the reference is filled now, not dropped");
    }

    [Fact]
    public void The_display_label_drops_and_is_measured()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Gaps.Should().Contain(g =>
            g.Field == "lot_no" && g.Kind == VocabularyGapKind.IdentityDisplay && g.Construct.Contains("로트 번호"));
    }

    [Fact]
    public void A_lookup_whose_target_model_is_not_in_the_document_stays_a_gap_that_says_so()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Hints[0].Fields.Should().NotContain(f => f.Name == "inspector_name",
            "Employee is not declared here, so the field its records are identified by is unknown");
        result.Gaps.Should().Contain(g =>
            g.Field == "inspector_name" && g.Kind == VocabularyGapKind.Derived
            && g.Construct == "lookup inspector_id.name" && g.Detail.Contains("Employee"));
    }

    private const string StaffedInspectionForm =
        """
        ## Employee

        - id: identifier @pk @generated
        - name: string(100)
        - dept_code: string(10) @reference(Department)

        ## Department

        - code: string(10) @pk
        - title: string

        ## Shift

        - site: string(10) @pk
        - no: integer @pk
        - lead: string

        ## Inspection

        - id: identifier @pk @generated
        - lot_no: string(50)
        - inspector_id: identifier @reference(Employee)
        - shift_site: string(10) @reference(Shift)

        ### Lookup
        - inspector_name: string @lookup(inspector_id.name)
        - inspector_dept: string @lookup(inspector_id.dept_code.title)
        - lot_owner: string @lookup(lot_no.name)
        - shift_lead: string @lookup(shift_site.lead)
        - inspector_badge: string @lookup(inspector_id.badge)
        """;

    [Fact]
    public void A_lookup_over_a_reference_is_declared_as_a_computed_reference()
    {
        var result = M3lHintAdapter.Adapt(StaffedInspectionForm);

        var inspection = result.Hints.Single(h => h.Type.Value == "inspection");
        var field = inspection.Fields.Single(f => f.Name == "inspector_name");
        field.Type.Should().Be(ColumnType.Text);
        field.Binding.Should().Be(FieldBinding.Reference);
        field.Target.Should().Be(new EntityRef(
            FormTypeRef.Create("employee"), "name", TargetLookup.Field("id"), "inspector_id"),
            "the path names the field here that carries the key and the target's field to read; the target's @pk is what the key is matched against");
        result.Gaps.Should().NotContain(g => g.Field == "inspector_name");
    }

    [Theory]
    [InlineData("inspector_dept", "path of any other length")]
    [InlineData("lot_owner", "not a stored field of Inspection declared @reference")]
    [InlineData("shift_lead", "declares 2 @pk fields")]
    [InlineData("inspector_badge", "'badge' is not a stored field of Employee")]
    public void A_lookup_the_document_does_not_say_enough_to_compute_is_a_gap_with_its_reason(string name, string reason)
    {
        var result = M3lHintAdapter.Adapt(StaffedInspectionForm);

        result.Hints.Single(h => h.Type.Value == "inspection").Fields.Should().NotContain(f => f.Name == name);
        result.Gaps.Should().ContainSingle(g => g.Field == name)
            .Which.Should().Match<VocabularyGap>(g => g.Kind == VocabularyGapKind.Derived && g.Detail.Contains(reason));
    }

    [Fact]
    public async Task A_lookup_imported_from_m3l_reads_the_targets_value_when_projected()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();

        // Declared in the document's order: the target before the form type that reads it.
        var adapted = M3lHintAdapter.Adapt(
            """
            ## Employee

            - id: identifier @pk
            - name: string(100)

            ## Inspection

            - lot_no: string(50)
            - inspector_id: identifier @reference(Employee)

            ### Lookup
            - inspector_name: string @lookup(inspector_id.name)
            """);
        foreach (var declared in adapted.Hints)
        {
            hints.Declare(declared);
        }

        var employee = FormTypeRef.Create("employee");
        var inspection = FormTypeRef.Create("inspection");
        var kim = Guid.NewGuid();
        await engine.AcceptAsync(employee, DocumentBody.Parse($$"""{"id":"{{kim}}","name":"Kim"}"""), cancellationToken: ct);
        await engine.AcceptAsync(inspection, DocumentBody.Parse($$"""{"lot_no":"L-1","inspector_id":"{{kim}}"}"""), cancellationToken: ct);
        await engine.ProjectAsync(employee, ct);
        var projected = await engine.ProjectAsync(inspection, ct);

        projected.UnresolvedReferences.Should().BeEmpty("the lookup carries its pair");
        var rows = (await engine.QueryAsync(inspection, new QuerySpec(), ct)).Rows;
        rows.Should().ContainSingle().Which.Fields["inspector_name"].Should().Be("Kim");
    }

    [Fact]
    public void Binding_modes_fill_the_time_axis_of_the_field()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        var fields = result.Hints[0].Fields.ToDictionary(f => f.Name);
        // soft binding => reads true now
        fields["unit"].Binding.Should().Be(FieldBinding.Reference);
        fields["unit"].Target!.Entity.Value.Should().Be("master_item");
        fields["unit"].Target!.ValueField.Should().Be("Key");
        // hard binding => fixed then
        fields["approved_price"].Binding.Should().Be(FieldBinding.Snapshot);
        fields["approved_price"].Target!.Entity.Value.Should().Be("price_book");

        result.Gaps.Should().NotContain(g => g.Kind == VocabularyGapKind.TimeBinding,
            "the binding axis is filled now, not dropped");
    }

    [Fact]
    public void The_remaining_gaps_are_only_the_genuinely_uncarried_axes()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        // After the vocabulary absorbed relations and time-binding, what remains a gap is what the
        // declaration vocabulary genuinely does not carry — human labels, derived fields, declared
        // value constraints — and what the document does not say: which target record a soft binding
        // reads, or a lookup whose target model is declared elsewhere.
        result.Gaps.Select(g => g.Kind).Distinct().Should().OnlyContain(k =>
            k == VocabularyGapKind.IdentityDisplay
            || k == VocabularyGapKind.Derived
            || k == VocabularyGapKind.Constraint
            || k == VocabularyGapKind.Unresolved
            || k == VocabularyGapKind.ReferenceLookup);
    }

    [Fact]
    public void A_soft_binding_is_named_at_import_as_a_reference_without_a_lookup()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        var references = result.Hints.SelectMany(h => h.Fields).Where(f => f.Binding == FieldBinding.Reference).ToList();
        references.Should().NotBeEmpty("the fixture has a soft binding");
        references.Should().OnlyContain(f => f.Target!.Lookup == null, "a soft binding names a column, not a record; none of these is a lookup field");
        result.Gaps.Where(g => g.Kind == VocabularyGapKind.ReferenceLookup).Select(g => g.Field)
            .Should().BeEquivalentTo(references.Select(f => f.Name), "each one is named before a projection reports it empty");
    }

    [Fact]
    public void Enum_membership_degrades_to_text_and_is_measured()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Hints[0].Fields.Single(f => f.Name == "status").Type.Should().Be(ColumnType.Text);
        result.Gaps.Should().Contain(g =>
            g.Field == "status" && g.Kind == VocabularyGapKind.Constraint && g.Construct == "inline enum");
    }

    private const string NumericLadderForm =
        """
        ## Measurement

        - id: identifier @pk
        - flags: byte
        - revision: short
        - counted: integer
        - total: long
        - ratio: float
        - precise: double
        - amount: decimal(10,2)
        - price: money
        - share: percentage
        - payload: binary
        """;

    // The catalog states a width for every numeric rung, and the flat vocabulary has exactly two
    // numeric slots to put them in. Integer is emitted as a 64-bit column, so the whole integer
    // ladder fits it without losing a value; the approximate and fixed-point types share Decimal.
    // Neither collapse is a gap — a gap here would claim the vocabulary cannot carry a value it
    // demonstrably can.
    [Theory]
    [InlineData("flags", ColumnType.Integer)]
    [InlineData("revision", ColumnType.Integer)]
    [InlineData("counted", ColumnType.Integer)]
    [InlineData("total", ColumnType.Integer)]
    [InlineData("ratio", ColumnType.Decimal)]
    [InlineData("precise", ColumnType.Decimal)]
    [InlineData("amount", ColumnType.Decimal)]
    [InlineData("price", ColumnType.Decimal)]
    [InlineData("share", ColumnType.Decimal)]
    public void Every_numeric_type_maps_to_a_numeric_slot_without_a_gap(string field, ColumnType expected)
    {
        var result = M3lHintAdapter.Adapt(NumericLadderForm);

        result.Hints[0].Fields.Single(f => f.Name == field).Type.Should().Be(expected);
        result.Gaps.Should().NotContain(g => g.Field == field && g.Kind == VocabularyGapKind.Unresolved);
    }

    // The boundary the mapping stops at, pinned on purpose: binary is the one catalog type with no
    // slot in the flat vocabulary, so it degrades to Text and is counted. Were it ever mapped
    // silently, this measurement would stop reporting a demand that is real.
    [Fact]
    public void Binary_has_no_slot_and_stays_a_measured_gap()
    {
        var result = M3lHintAdapter.Adapt(NumericLadderForm);

        result.Hints[0].Fields.Single(f => f.Name == "payload").Type.Should().Be(ColumnType.Text);
        result.Gaps.Should().Contain(g =>
            g.Field == "payload" && g.Kind == VocabularyGapKind.Unresolved && g.Construct == "type binary");
    }

    private const string ParameterisedAndArrayForm =
        """
        ## Catalogue

        - id: identifier @pk
        - code: string(50)
        - price: decimal(10,2)
        - tags: string?[]
        """;

    // A declared parameter is a bound on the value, and the flat vocabulary has one type per field
    // and nowhere to put the bound. Dropping it is expected; dropping it *uncounted* is not — the
    // gap list is the measurement, so a loss that never reaches it cannot be designed against.
    [Fact]
    public void Declared_type_parameters_are_measured_not_silently_dropped()
    {
        var result = M3lHintAdapter.Adapt(ParameterisedAndArrayForm);

        var fields = result.Hints[0].Fields.ToDictionary(f => f.Name);
        fields["code"].Type.Should().Be(ColumnType.Text, "the parameter drops, the type still maps");
        fields["price"].Type.Should().Be(ColumnType.Decimal);
        result.Gaps.Should().Contain(g =>
            g.Field == "code" && g.Kind == VocabularyGapKind.Constraint && g.Construct == "string(50)");
        result.Gaps.Should().Contain(g =>
            g.Field == "price" && g.Kind == VocabularyGapKind.Constraint && g.Construct == "decimal(10,2)");
    }

    // An array still lands in one JSONB column — that part is unchanged. What is new is that the
    // element type and its nullability are counted on the way out instead of vanishing.
    [Fact]
    public void An_array_field_records_what_the_jsonb_column_cannot_say()
    {
        var result = M3lHintAdapter.Adapt(ParameterisedAndArrayForm);

        result.Hints[0].Fields.Single(f => f.Name == "tags").Type.Should().Be(ColumnType.Jsonb);
        result.Gaps.Should().Contain(g =>
            g.Field == "tags" && g.Kind == VocabularyGapKind.Unresolved && g.Construct == "array of string");
    }

    private const string OwnedAndComposedForm =
        """
        # Prefix: acme

        ## Party
        - name: string
        - code: string

        ## Customer ::aspect(Party)
        - tier: integer

        ## Vendor ::subtype(Party)
        - rating: decimal

        ## Party ::extend
        - region: string
        """;

    // An extension's fields are merged into the target by the parser, so the column itself arrives —
    // what drops is which owner contributed it.
    [Fact]
    public void An_extension_field_keeps_its_column_and_measures_the_lost_owner()
    {
        var result = M3lHintAdapter.Adapt(OwnedAndComposedForm);

        var party = result.Hints.Single(h => h.TableName == "party");
        party.Fields.Select(f => f.Name).Should().Equal("name", "code", "region");
        result.Gaps.Should().ContainSingle(g => g.Field != null && g.Kind == VocabularyGapKind.Unresolved)
            .Which.Should().Match<VocabularyGap>(g => g.Field == "region" && g.Construct == "extended by acme");
    }

    // ::aspect / ::subtype are ordinary models that name a base. Emitting one as a table of its own
    // fields is the same composition loss inheritance already records — it must not pass silently.
    [Fact]
    public void A_base_model_link_is_measured_like_inheritance()
    {
        var result = M3lHintAdapter.Adapt(OwnedAndComposedForm);

        result.Hints.Single(h => h.TableName == "customer").Fields.Select(f => f.Name).Should().Equal("tier");
        result.Gaps.Should().Contain(g =>
            g.Model == "Customer" && g.Field == null && g.Construct == "aspect of Party");
        result.Gaps.Should().Contain(g =>
            g.Model == "Vendor" && g.Field == null && g.Construct == "subtype of Party");
    }

    [Fact]
    public void The_declared_owner_is_measured_on_every_model_it_qualifies()
    {
        var result = M3lHintAdapter.Adapt(OwnedAndComposedForm);

        result.Gaps.Where(g => g.Construct == "owner acme").Select(g => g.Model)
            .Should().BeEquivalentTo("Party", "Customer", "Vendor");
        result.Hints.Single(h => h.TableName == "party").Type.Value.Should().Be("party",
            "the form type is still the bare model name — that is the loss being measured");
    }

    [Fact]
    public void A_document_without_owners_or_bases_records_none_of_these_gaps()
    {
        var result = M3lHintAdapter.Adapt(InspectionForm);

        result.Gaps.Should().NotContain(g =>
            g.Construct.StartsWith("owner ") || g.Construct.StartsWith("extended by ")
            || g.Construct.Contains(" of ") && g.Field == null);
    }
}
