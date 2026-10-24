using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Formdown;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Formdown;

/// <summary>
/// The fixture <c>inspection.schema.json</c> is what formdown core 0.21.0's <c>getSchema()</c> returned for
/// <c>inspection.fd</c> — generated, not written by hand, so these tests read the shape formdown emits.
/// </summary>
public class FormdownAdapterTests
{
    private static readonly FormTypeRef Inspection = FormTypeRef.Create("inspection");

    private static string Schema() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Formdown", "inspection.schema.json"));

    private static FormdownAdapter.SchemaResult Adapted() =>
        FormdownAdapter.AdaptSchema(Schema(), Inspection, "inspections");

    [Fact]
    public void Each_data_field_becomes_one_hint_in_form_order_and_buttons_do_not()
    {
        var hints = Adapted().Hints;

        hints.Type.Should().Be(Inspection);
        hints.TableName.Should().Be("inspections");
        hints.Fields.Select(f => f.Name).Should().Equal(
            "lot_no", "inspected_on", "qty", "unit_price", "total", "passed", "result",
            "grade", "note", "shift_start", "photo", "lotCode", "supplier");
    }

    [Fact]
    public void Types_follow_what_the_input_hands_over()
    {
        var types = Adapted().Hints.Fields.ToDictionary(f => f.Name, f => f.Type);

        types["lot_no"].Should().Be(ColumnType.Text);
        types["inspected_on"].Should().Be(ColumnType.Date, "a date field is a day, not a point in time");
        types["qty"].Should().Be(ColumnType.Decimal, "a step is an input rule, not the type of what is stored");
        types["unit_price"].Should().Be(ColumnType.Decimal);
        types["total"].Should().Be(ColumnType.Decimal);
        types["passed"].Should().Be(ColumnType.Boolean, "a single checkbox is on or off");
        types["result"].Should().Be(ColumnType.Text);
        types["shift_start"].Should().Be(ColumnType.Text, "a time of day is not a point in time");
        types["photo"].Should().Be(ColumnType.Text);
    }

    [Fact]
    public void A_checkbox_group_is_a_section_with_one_row_per_checked_option()
    {
        var adapted = Adapted();

        var relation = adapted.Hints.Relations.Should().ContainSingle().Which;
        relation.Should().Be(new RelationHint("defects", RelationKind.Child, FormTypeRef.Create("inspection.defects"), "parent", "defects"));
        var section = adapted.Sections.Should().ContainSingle().Which;
        section.TableName.Should().Be("inspections_defects");
        section.Fields.Select(f => (f.Name, f.Type, f.Nullable)).Should().Equal(
            ("parent", ColumnType.Text, false), (FormdownAdapter.SectionValueField, ColumnType.Text, false));
        adapted.Declarations.Should().Equal(section, adapted.Hints);
    }

    [Fact]
    public void Required_fields_are_not_nullable()
    {
        var fields = Adapted().Hints.Fields.ToDictionary(f => f.Name);

        fields["lot_no"].Nullable.Should().BeFalse();
        fields["inspected_on"].Nullable.Should().BeFalse();
        fields["qty"].Nullable.Should().BeTrue();
    }

    [Fact]
    public void A_written_label_is_the_business_name_and_the_label_formdown_makes_from_the_name_is_not()
    {
        var fields = Adapted().Hints.Fields.ToDictionary(f => f.Name);

        fields["lot_no"].BusinessName.Should().Be("로트 번호");
        fields["lotCode"].BusinessName.Should().BeNull("«Lot Code» is the label formdown made from the name");
        fields.Values.Where(f => f.BusinessName is not null).Should().ContainSingle();
    }

    [Theory]
    [InlineData("lot_no", "Lot No")]
    [InlineData("lotCode", "Lot Code")]
    [InlineData("qty", "Qty")]
    [InlineData("HTTPMode", "Httpmode")]
    public void The_default_label_is_formdowns(string name, string label) =>
        FormdownAdapter.DefaultLabel(name).Should().Be(label);

    [Fact]
    public void Value_sets_rules_and_formulas_are_counted_as_gaps()
    {
        var constructs = Adapted().Gaps.Select(g => (g.Field, g.Kind, g.Construct)).ToList();

        constructs.Should().Contain([
            ("lot_no", VocabularyGapKind.Constraint, "maxlength=50"),
            ("qty", VocabularyGapKind.Constraint, "min=0"),
            ("unit_price", VocabularyGapKind.Constraint, "step=0.01"),
            ("total", VocabularyGapKind.Derived, "compute \"qty * unit_price\""),
            ("result", VocabularyGapKind.Constraint, "options [pass=합격, fail=불합격]"),
            ("defects", VocabularyGapKind.Constraint, "options [scratch, dent, stain]"),
            ("grade", VocabularyGapKind.Constraint, "options [A, B, C] + other"),
            ("shift_start", VocabularyGapKind.Unresolved, "type time"),
            ("photo", VocabularyGapKind.Constraint, "accept=image/*"),
            ("photo", VocabularyGapKind.Unresolved, "type file"),
            ("lotCode", VocabularyGapKind.Constraint, "pattern=^[A-Z]{2}-\\d+$"),
        ]);
    }

    [Fact]
    public void The_hints_and_the_gaps_say_nothing_of_a_relation_the_schema_does_not_carry()
    {
        // inspection.fd declares `@supplier -> Suppliers`, and formdown's parser reads the relation,
        // but getSchema() does not hand it over: the adapter cannot report what it never receives.
        var adapted = Adapted();

        adapted.Hints.Relations.Should().NotContain(r => r.Name == "supplier");
        adapted.Gaps.Should().NotContain(g => g.Field == "supplier");
    }

    [Fact]
    public void Form_data_is_typed_by_the_schema()
    {
        var body = FormdownAdapter.AdaptData(
            """
            {"lot_no":"L-1","inspected_on":"2026-10-20","qty":"12","unit_price":"2.50","total":"30",
             "passed":true,"result":"pass","defects":["dent"],"grade":"B","note":"",
             "shift_start":"08:30","photo":"","lotCode":"AB-1","supplier":"S-1","extra":"kept"}
            """,
            Adapted().Hints);

        var root = body.Root;
        root.GetProperty("qty").GetDecimal().Should().Be(12m);
        root.GetProperty("unit_price").GetDecimal().Should().Be(2.50m);
        root.GetProperty("total").GetDecimal().Should().Be(30m);
        root.GetProperty("passed").GetBoolean().Should().BeTrue();
        root.GetProperty("defects")[0].GetString().Should().Be("dent");
        root.GetProperty("note").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, "a form hands over an unwritten box as an empty string");
        root.GetProperty("inspected_on").GetString().Should().Be("2026-10-20");
        root.GetProperty("extra").GetString().Should().Be("kept", "the raw document keeps what the form sent");
    }

    [Fact]
    public void An_empty_number_or_date_is_no_value_and_an_unreadable_number_is_kept_as_sent()
    {
        var body = FormdownAdapter.AdaptData(
            """{"qty":"","inspected_on":" ","unit_price":"1,234.5"}""",
            Adapted().Hints);

        var root = body.Root;
        root.GetProperty("qty").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        root.GetProperty("inspected_on").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        root.GetProperty("unit_price").GetString().Should().Be("1,234.5", "a grouping separator is not read as a number");
    }

    [Fact]
    public void An_unchosen_option_or_an_unwritten_box_is_no_value_and_a_written_one_is_kept()
    {
        var body = FormdownAdapter.AdaptData(
            """{"result":"","grade":"","note":"","lot_no":" L-1 ","defects":[]}""",
            Adapted().Hints);

        var root = body.Root;
        root.GetProperty("result").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, "no option was chosen");
        root.GetProperty("grade").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        root.GetProperty("note").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        root.GetProperty("lot_no").GetString().Should().Be(" L-1 ", "a written text is kept as written");
        root.GetProperty("defects").GetArrayLength().Should().Be(0, "an empty checked list is an answer: none of them");
    }

    [Fact]
    public async Task A_filled_form_projects_with_its_types()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();

        var adapted = Adapted();
        foreach (var declaration in adapted.Declarations)
        {
            hints.Declare(declaration);
        }

        await engine.AcceptAsync(Inspection, FormdownAdapter.AdaptData(
            """{"lot_no":"L-1","inspected_on":"2026-10-20","qty":"12","unit_price":"2.50","total":"30","passed":true,"defects":["dent","stain"]}""",
            adapted.Hints), cancellationToken: ct);
        await engine.AcceptAsync(Inspection, FormdownAdapter.AdaptData(
            """{"lot_no":"L-2","inspected_on":"2026-10-21","qty":"8","unit_price":"3","total":"24","passed":false,"defects":[]}""",
            adapted.Hints), cancellationToken: ct);
        var projected = await engine.ProjectAsync(Inspection, ct);

        // The checked options are rows: each can be filtered on, grouped and counted, and the form with
        // none checked adds no row while a form that never had the group is counted as missing it.
        await engine.AcceptAsync(Inspection, FormdownAdapter.AdaptData(
            """{"lot_no":"L-3","inspected_on":"2026-10-22","qty":"1","unit_price":"1","total":"1","passed":true}""",
            adapted.Hints), cancellationToken: ct);
        var defects = adapted.Sections[0].Type;
        var sectionProjection = await engine.ProjectAsync(defects, ct);
        sectionProjection.AbsentFieldCounts.Should().Equal(new Dictionary<string, int> { ["defects"] = 1 });
        var dents = await engine.QueryAsync(defects, new QuerySpec([new FieldFilter(FormdownAdapter.SectionValueField, FilterOperator.Equal, "dent")]), ct);
        dents.Rows.Should().ContainSingle();
        var perOption = await engine.AggregateAsync(defects, new AggregateSpec(GroupBy: [FormdownAdapter.SectionValueField]), ct);
        perOption.Groups.Select(g => ((string?)g.Key[FormdownAdapter.SectionValueField], g.Count)).Should().BeEquivalentTo([("dent", 1), ("stain", 1)]);

        projected.Should().NotBeNull();
        (await engine.GetProjectionFieldSkipsAsync(Inspection, ct)).Should().BeEmpty("every value the form sent fits its column");
        var totals = await engine.AggregateAsync(Inspection, new AggregateSpec(
            Measures: [AggregateMeasure.Sum("qty"), AggregateMeasure.Sum("unit_price")]), ct);
        var measures = totals.Groups.Should().ContainSingle().Which.Measures;
        measures[0].Value.Should().Be(20m);
        measures[1].Value.Should().Be(5.50m);
    }

    [Fact]
    public async Task A_fraction_in_a_document_that_did_not_come_through_the_form_is_kept_in_ranges_and_sums()
    {
        // qty has no step, so the form itself takes only whole numbers; an imported document is not
        // held to the form's rules, and its 2.5 is a value the table must still count.
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();

        var adapted = Adapted();
        hints.Declare(adapted.Hints);
        await engine.AcceptAsync(Inspection, FormdownAdapter.AdaptData(
            """{"lot_no":"L-1","inspected_on":"2026-10-20","qty":"12"}""", adapted.Hints), cancellationToken: ct);
        await engine.AcceptAsync(Inspection, DocumentBody.Parse(
            """{"lot_no":"L-2","inspected_on":"2026-10-21","qty":2.5}"""), cancellationToken: ct);
        await engine.ProjectAsync(Inspection, ct);

        (await engine.GetProjectionFieldSkipsAsync(Inspection, ct)).Should().BeEmpty();
        var totals = await engine.AggregateAsync(Inspection, new AggregateSpec(
            Measures: [AggregateMeasure.Sum("qty")]), ct);
        totals.Groups.Should().ContainSingle().Which.Measures[0].Value.Should().Be(14.5m);
    }

    [Fact]
    public void A_toggle_is_not_a_formdown_type_and_degrades_like_any_unknown_one()
    {
        // formdown 0.22.0 writes a switch as a checkbox (`switch: true`); `toggle` is no longer a type.
        var adapted = FormdownAdapter.AdaptSchema(
            """{"on":{"type":"checkbox","switch":true},"old":{"type":"toggle"}}""", Inspection, "inspections");
        var types = adapted.Hints.Fields.ToDictionary(f => f.Name, f => f.Type);

        types["on"].Should().Be(ColumnType.Boolean);
        types["old"].Should().Be(ColumnType.Text);
        adapted.Gaps.Should().Contain(g => g.Field == "old" && g.Kind == VocabularyGapKind.Unresolved && g.Construct == "type toggle");
    }

    [Theory]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("""{"a":1}""", "maps each field name to an object")]
    [InlineData("""{"a":{"label":"A"}}""", "has no type")]
    [InlineData("{", "not valid JSON")]
    public void Something_that_is_not_a_formdown_schema_is_refused(string json, string message)
    {
        var act = () => FormdownAdapter.AdaptSchema(json, Inspection, "inspections");

        act.Should().Throw<FormatException>().WithMessage($"*{message}*");
    }

    // conditional.schema.json and the states below are what formdown core 0.25.0's getSchema() and
    // conditionStates() returned for conditional.fd and the data beside them — generated, not hand-written.
    private static readonly FormTypeRef Corrective = FormTypeRef.Create("corrective");

    private static FormdownAdapter.SchemaResult AdaptedConditional() =>
        FormdownAdapter.AdaptSchema(
            File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Formdown", "conditional.schema.json")),
            Corrective, "correctives");

    private const string PassData = """{"lot_no":"L-1","result":"pass","reason":"x","action":"re-weld","defects":["dent"],"note":""}""";

    private const string PassStates = """
        {"lot_no":{"visible":true,"enabled":true,"required":false},"result":{"visible":true,"enabled":true,"required":false},
         "reason":{"visible":true,"enabled":false,"required":false},"action":{"visible":false,"enabled":true,"required":false},
         "defects":{"visible":false,"enabled":true,"required":false},"note":{"visible":true,"enabled":true,"required":false}}
        """;

    [Fact]
    public void A_field_its_conditions_hid_or_disabled_is_left_out_of_the_document()
    {
        // The writer chose "fail", filled the follow-up fields, then chose "pass": the form still hands
        // over what was written in the fields that are now hidden or disabled.
        var root = FormdownAdapter.AdaptData(PassData, AdaptedConditional().Hints, PassStates).Root;

        root.TryGetProperty("action", out _).Should().BeFalse("a hidden field holds no answer");
        root.TryGetProperty("defects", out _).Should().BeFalse("a hidden checkbox group checked nothing");
        root.TryGetProperty("reason", out _).Should().BeFalse("a disabled field could not be filled in");
        root.GetProperty("note").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null, "a visible field left blank is still no value");
        root.GetProperty("lot_no").GetString().Should().Be("L-1");
    }

    [Fact]
    public void Without_condition_states_every_field_the_form_sent_is_kept()
    {
        var root = FormdownAdapter.AdaptData(PassData, AdaptedConditional().Hints).Root;

        root.GetProperty("action").GetString().Should().Be("re-weld");
        root.GetProperty("reason").GetString().Should().Be("x");
        root.GetProperty("defects").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public void A_field_a_condition_can_take_away_is_optional_and_its_condition_is_reported()
    {
        var adapted = AdaptedConditional();
        var nullable = adapted.Hints.Fields.ToDictionary(f => f.Name, f => f.Nullable);

        nullable["lot_no"].Should().BeFalse("no condition can take it away");
        nullable["reason"].Should().BeTrue("required, but missing from every form filled while it was disabled");
        nullable["action"].Should().BeTrue("required, but missing from every form filled while it was hidden");
        adapted.Gaps.Should().Contain(g => g.Field == "action" && g.Kind == VocabularyGapKind.Constraint && g.Construct == "visible-if result=fail");
        adapted.Gaps.Should().Contain(g => g.Field == "reason" && g.Construct == "disabled-if result=pass");
    }

    [Fact]
    public async Task A_hidden_field_projects_as_absent_and_a_blank_one_as_null()
    {
        var ct = TestContext.Current.CancellationToken;
        var services = new ServiceCollection();
        services.AddFormbaseInMemory();
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<InMemoryFieldHintSource>();
        var adapted = AdaptedConditional();
        foreach (var declaration in adapted.Declarations)
        {
            hints.Declare(declaration);
        }

        await engine.AcceptAsync(Corrective, FormdownAdapter.AdaptData(PassData, adapted.Hints, PassStates), cancellationToken: ct);
        await engine.AcceptAsync(Corrective, FormdownAdapter.AdaptData(
            """{"lot_no":"L-2","result":"fail","reason":"crack","action":"","defects":[],"note":""}""", adapted.Hints,
            """{"lot_no":{"visible":true,"enabled":true,"required":false},"result":{"visible":true,"enabled":true,"required":false},"reason":{"visible":true,"enabled":true,"required":false},"action":{"visible":true,"enabled":true,"required":false},"defects":{"visible":true,"enabled":true,"required":false},"note":{"visible":true,"enabled":true,"required":false}}"""),
            cancellationToken: ct);
        var projected = await engine.ProjectAsync(Corrective, ct);

        projected.Skipped.Should().BeEmpty("a required field a condition hid does not cost the row");
        projected.AbsentFieldCounts.Should().Contain("action", 1).And.Contain("reason", 1);
        var blankActions = await engine.QueryAsync(Corrective, new QuerySpec([new FieldFilter("action", FilterOperator.IsNull, null)]), ct);
        blankActions.Rows.Should().HaveCount(2, "the projected column holds no value for either — the absence count tells them apart");
    }

    [Theory]
    [InlineData("[]", "must be a JSON object")]
    [InlineData("""{"action":true}""", "map each field name to an object")]
    public void Condition_states_that_are_not_formdowns_are_refused(string json, string message)
    {
        var act = () => FormdownAdapter.AdaptData(PassData, AdaptedConditional().Hints, json);

        act.Should().Throw<FormatException>().WithMessage($"*{message}*");
    }
}
