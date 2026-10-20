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
            "lot_no", "inspected_on", "qty", "unit_price", "total", "passed", "result", "defects",
            "grade", "note", "shift_start", "photo", "lotCode", "supplier");
    }

    [Fact]
    public void Types_follow_what_the_input_hands_over()
    {
        var types = Adapted().Hints.Fields.ToDictionary(f => f.Name, f => f.Type);

        types["lot_no"].Should().Be(ColumnType.Text);
        types["inspected_on"].Should().Be(ColumnType.Timestamp);
        types["qty"].Should().Be(ColumnType.Integer, "a number input with no step takes whole numbers");
        types["unit_price"].Should().Be(ColumnType.Decimal, "a fractional step admits fractions");
        types["total"].Should().Be(ColumnType.Integer);
        types["passed"].Should().Be(ColumnType.Boolean, "a single checkbox is on or off");
        types["result"].Should().Be(ColumnType.Text);
        types["defects"].Should().Be(ColumnType.Jsonb, "a checkbox group hands over the list checked");
        types["shift_start"].Should().Be(ColumnType.Text, "a time of day is not a point in time");
        types["photo"].Should().Be(ColumnType.Text);
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
    public void A_written_label_is_a_gap_and_the_label_formdown_makes_from_the_name_is_not()
    {
        var labels = Adapted().Gaps.Where(g => g.Kind == VocabularyGapKind.IdentityDisplay).ToList();

        labels.Should().ContainSingle().Which.Should().Be(new VocabularyGap(
            "inspection", "lot_no", VocabularyGapKind.IdentityDisplay, "label \"로트 번호\"",
            "A human display label has no slot; SourceKey/Name are machine names, not labels."));
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
            ("defects", VocabularyGapKind.Unresolved, "checkbox group"),
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

        adapted.Hints.Relations.Should().BeNull();
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
        root.GetProperty("qty").GetInt64().Should().Be(12);
        root.GetProperty("unit_price").GetDecimal().Should().Be(2.50m);
        root.GetProperty("total").GetInt64().Should().Be(30);
        root.GetProperty("passed").GetBoolean().Should().BeTrue();
        root.GetProperty("defects")[0].GetString().Should().Be("dent");
        root.GetProperty("note").GetString().Should().BeEmpty("an empty text is a text");
        root.GetProperty("inspected_on").GetString().Should().Be("2026-10-20");
        root.GetProperty("extra").GetString().Should().Be("kept", "the raw document keeps what the form sent");
    }

    [Fact]
    public void An_empty_number_or_date_is_no_value_and_an_unreadable_number_is_kept_as_sent()
    {
        var body = FormdownAdapter.AdaptData(
            """{"qty":"","inspected_on":" ","unit_price":"1,234.5","total":"7.5"}""",
            Adapted().Hints);

        var root = body.Root;
        root.GetProperty("qty").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        root.GetProperty("inspected_on").ValueKind.Should().Be(System.Text.Json.JsonValueKind.Null);
        root.GetProperty("unit_price").GetString().Should().Be("1,234.5", "a grouping separator is not read as a number");
        root.GetProperty("total").GetString().Should().Be("7.5", "a whole-number field does not round a fraction");
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
        hints.Declare(adapted.Hints);
        await engine.AcceptAsync(Inspection, FormdownAdapter.AdaptData(
            """{"lot_no":"L-1","inspected_on":"2026-10-20","qty":"12","unit_price":"2.50","total":"30","passed":true,"defects":["dent","stain"]}""",
            adapted.Hints), cancellationToken: ct);
        await engine.AcceptAsync(Inspection, FormdownAdapter.AdaptData(
            """{"lot_no":"L-2","inspected_on":"2026-10-21","qty":"8","unit_price":"3","total":"24","passed":false,"defects":[]}""",
            adapted.Hints), cancellationToken: ct);
        var projected = await engine.ProjectAsync(Inspection, ct);

        projected.Should().NotBeNull();
        (await engine.GetProjectionFieldSkipsAsync(Inspection, ct)).Should().BeEmpty("every value the form sent fits its column");
        var totals = await engine.AggregateAsync(Inspection, new AggregateSpec(
            Measures: [AggregateMeasure.Sum("qty"), AggregateMeasure.Sum("unit_price")]), ct);
        var measures = totals.Groups.Should().ContainSingle().Which.Measures;
        measures[0].Value.Should().Be(20m);
        measures[1].Value.Should().Be(5.50m);
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
}
