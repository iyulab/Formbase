using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Contracts;

/// <summary>
/// What every <see cref="IProjectionStore"/> must answer for a repeated section's table: the rows of one
/// parent document share its document, key and watermark, so a store has to keep them apart by position
/// wherever it names a row — a query, the records behind an aggregate, and the source of a reference that
/// reads one — and has to order them the same way the others do.
/// </summary>
public abstract class SectionStoreContractTests
{
    protected abstract IProjectionStore CreateStore();

    // Unique per test instance so live stores sharing one database don't collide across tests.
    private readonly string _suffix = Guid.NewGuid().ToString("N")[..12];

    private FormTypeRef Order => FormTypeRef.Create("o_" + _suffix);
    private FormTypeRef Line => FormTypeRef.Create("l_" + _suffix);
    private FormTypeRef Check => FormTypeRef.Create("c_" + _suffix);

    private sealed record Harness(FormbaseEngine Engine, InMemoryFieldHintSource Hints);

    private Harness Engine(IProjectionStore store)
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var state = new InMemoryProjectionState();
        var proposer = new HintSchemaProposer(hints);
        hints.Declare(new FormTypeHints(Line, Line.Value,
        [
            new FieldHint("order_no", ColumnType.Text),
            new FieldHint("code", ColumnType.Text),
            new FieldHint("product", ColumnType.Text),
            new FieldHint("qty", ColumnType.Integer),
        ]));
        hints.Declare(new FormTypeHints(Order, Order.Value,
            [new FieldHint("order_no", ColumnType.Text), new FieldHint("customer", ColumnType.Text)],
            [new RelationHint("lines", RelationKind.Child, Line, "order_no", "lines")]));
        var engine = new FormbaseEngine(
            new IntakeService(raw, hints),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state,
            proposer);
        return new Harness(engine, hints);
    }

    private const string OrderBody =
        """{"order_no":"A-1","customer":"Kim","lines":[{"code":"B","product":"bolt","qty":3},{"code":"N","product":"nut","qty":5},{"code":"B","product":"bolt-long","qty":1}]}""";

    [Fact]
    public async Task A_section_row_is_named_by_its_position_and_read_in_position_order()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        var key = RecordKey.Create("ord-1");
        var original = await h.Engine.AcceptAsync(Order, DocumentBody.Parse(OrderBody), recordKey: key, cancellationToken: ct);
        await h.Engine.ProjectAsync(Order, ct);
        await h.Engine.ProjectAsync(Line, ct);

        var rows = await h.Engine.QueryAsync(Line, QuerySpec.All, ct);

        rows.Rows.Select(r => r.Fields["product"]).Should().Equal("bolt", "nut", "bolt-long");
        rows.Rows.Select(r => r.Record).Should().Equal(
            new RecordRef(original, key, 0), new RecordRef(original, key, 1), new RecordRef(original, key, 2));
        rows.Rows.Should().OnlyContain(r => (string?)r.Fields["order_no"] == "A-1");

        // Brought forward through the store's own replacement: the correction holds one line.
        var corrected = await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-1","lines":[{"code":"W","product":"washer"}]}"""), recordKey: key, cancellationToken: ct);
        (await h.Engine.ProjectAsync(Line, ct)).Mode.Should().Be(ProjectionMode.Incremental);
        (await h.Engine.QueryAsync(Line, QuerySpec.All, ct)).Rows.Select(r => r.Record).Should().Equal(new RecordRef(corrected, key, 0));
    }

    [Fact]
    public async Task The_records_behind_an_aggregate_of_a_section_name_each_row()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        var document = await h.Engine.AcceptAsync(Order, DocumentBody.Parse(OrderBody), cancellationToken: ct);
        await h.Engine.ProjectAsync(Line, ct);

        var groups = await h.Engine.AggregateAsync(Line, new AggregateSpec(GroupBy: ["code"], RecordsPerGroup: 10), ct);

        var bolts = groups.Groups.Single(g => (string?)g.Key["code"] == "B");
        bolts.Count.Should().Be(2);
        bolts.Records.Should().Equal(new RecordRef(document, null, 0), new RecordRef(document, null, 2));
        groups.Groups.Single(g => (string?)g.Key["code"] == "N").Records.Should().Equal(new RecordRef(document, null, 1));
    }

    private void DeclareCheck(Harness h, TargetLookup lookup, string via) =>
        h.Hints.Declare(new FormTypeHints(Check, Check.Value,
        [
            new FieldHint(via, ColumnType.Text),
            new FieldHint("product", ColumnType.Text, Binding: FieldBinding.Reference, Target: new EntityRef(Line, "product", lookup: lookup, viaField: via)),
        ]));

    [Fact]
    public async Task A_reference_to_a_section_row_by_record_reads_that_row_and_names_it()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        var key = RecordKey.Create("ord-1");
        var document = await h.Engine.AcceptAsync(Order, DocumentBody.Parse(OrderBody), recordKey: key, cancellationToken: ct);
        DeclareCheck(h, TargetLookup.Record, "line_ref");
        await h.Engine.AcceptAsync(Check, DocumentBody.Parse("""{"line_ref":"ord-1/1"}"""), cancellationToken: ct);
        await h.Engine.ProjectAsync(Line, ct);
        await h.Engine.ProjectAsync(Check, ct);

        var row = (await h.Engine.QueryAsync(Check, QuerySpec.All with { ReferenceSources = true }, ct)).Rows.Should().ContainSingle().Subject;

        row.Fields["product"].Should().Be("nut");
        row.References!["product"].Should().Be(new ReferenceSource(new RecordRef(document, key, 1), ReferenceOutcome.Resolved));
    }

    /// <summary>
    /// Two rows of one parent match the same code. Both are the latest accepted, so position decides —
    /// the first — and every store has to decide it the same way.
    /// </summary>
    [Fact]
    public async Task A_reference_matching_several_rows_of_one_parent_reads_the_first_by_position()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        var document = await h.Engine.AcceptAsync(Order, DocumentBody.Parse(OrderBody), cancellationToken: ct);
        DeclareCheck(h, TargetLookup.Field("code"), "code");
        await h.Engine.AcceptAsync(Check, DocumentBody.Parse("""{"code":"B"}"""), cancellationToken: ct);
        await h.Engine.ProjectAsync(Line, ct);
        await h.Engine.ProjectAsync(Check, ct);

        var row = (await h.Engine.QueryAsync(Check, QuerySpec.All with { ReferenceSources = true }, ct)).Rows.Should().ContainSingle().Subject;

        row.Fields["product"].Should().Be("bolt");
        row.References!["product"].Target.Should().Be(new RecordRef(document, null, 0));
    }

    [Fact]
    public async Task The_parent_and_its_section_project_side_by_side()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse(OrderBody), cancellationToken: ct);

        // Either order: the parent's child relation names the section's table, which may not exist yet.
        (await h.Engine.ProjectAsync(Order, ct)).Inserted.Should().Be(1);
        (await h.Engine.ProjectAsync(Line, ct)).Inserted.Should().Be(3);
        (await h.Engine.ProjectAsync(Order, ct)).Inserted.Should().Be(0, "nothing new was appended");

        (await h.Engine.QueryAsync(Order, QuerySpec.All, ct)).Rows.Should().ContainSingle().Which.Record.Row.Should().BeNull();
    }

    private FormTypeRef Tag => FormTypeRef.Create("t_" + _suffix);

    [Fact]
    public async Task A_section_of_values_holds_one_row_per_value_and_counts_a_parent_without_it()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        // One field beside the key: each item of the array is that field's value.
        h.Hints.Declare(new FormTypeHints(Tag, Tag.Value, [new FieldHint("order_no", ColumnType.Text), new FieldHint("tag", ColumnType.Text)]));
        h.Hints.Declare(new FormTypeHints(Order, Order.Value,
            [new FieldHint("order_no", ColumnType.Text), new FieldHint("customer", ColumnType.Text)],
            [new RelationHint("lines", RelationKind.Child, Line, "order_no", "lines"), new RelationHint("tags", RelationKind.Child, Tag, "order_no", "tags")]));
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-1","tags":["rush","fragile"]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-2","tags":["rush"]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-3","tags":[]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-4"}"""), cancellationToken: ct);

        var projected = await h.Engine.ProjectAsync(Tag, ct);

        projected.Inserted.Should().Be(3);
        projected.AbsentFieldCounts.Should().Equal(new Dictionary<string, int> { ["tags"] = 1 }, "an empty list is an answer; a missing one is not");
        var rush = await h.Engine.QueryAsync(Tag, new QuerySpec([new FieldFilter("tag", FilterOperator.Equal, "rush")], OrderBy: [new OrderKey("order_no")]), ct);
        rush.Rows.Select(r => (string?)r.Fields["order_no"]).Should().Equal("A-1", "A-2");
        var perTag = await h.Engine.AggregateAsync(Tag, new AggregateSpec(GroupBy: ["tag"]), ct);
        perTag.Groups.Select(g => ((string?)g.Key["tag"], g.Count)).Should().BeEquivalentTo([("fragile", 1), ("rush", 2)]);
    }

    [Fact]
    public async Task A_child_filter_keeps_the_parents_with_a_child_row_the_condition_keeps()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        h.Hints.Declare(new FormTypeHints(Tag, Tag.Value, [new FieldHint("order_no", ColumnType.Text), new FieldHint("tag", ColumnType.Text)]));
        h.Hints.Declare(new FormTypeHints(Order, Order.Value,
            [new FieldHint("order_no", ColumnType.Text), new FieldHint("customer", ColumnType.Text)],
            [new RelationHint("lines", RelationKind.Child, Line, "order_no", "lines"), new RelationHint("tags", RelationKind.Child, Tag, "order_no", "tags")]));
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-1","lines":[{"product":"nut","qty":5},{"product":"bolt","qty":1}],"tags":["rush","fragile"]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-2","lines":[{"product":"bolt","qty":2}],"tags":["rush","rush"]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-3","tags":[]}"""), cancellationToken: ct);
        foreach (var type in new[] { Order, Line, Tag })
        {
            await h.Engine.ProjectAsync(type, ct);
        }

        async Task<string?[]> Orders(params ChildFilter[] children) =>
            [.. (await h.Engine.QueryAsync(Order, new QuerySpec(OrderBy: [new OrderKey("order_no")], Children: children), ct)).Rows.Select(r => (string?)r.Fields["order_no"])];

        (await Orders(new ChildFilter("tags", FieldFilter.Equal("tag", "rush")))).Should().Equal(["A-1", "A-2"], "a parent with two matching rows is one row");
        (await Orders(new ChildFilter("tags", FieldFilter.Equal("tag", "rush")), new ChildFilter("tags", FieldFilter.Equal("tag", "fragile")))).Should().Equal("A-1");
        (await Orders(new ChildFilter("lines", new FieldFilter("qty", FilterOperator.GreaterThan, 4)))).Should().Equal("A-1");
        (await Orders(new ChildFilter("tags", FieldFilter.In("tag", ["urgent"])))).Should().BeEmpty();

        var counted = await h.Engine.AggregateAsync(Order, new AggregateSpec(Children: [new ChildFilter("tags", FieldFilter.Equal("tag", "rush"))]), ct);
        counted.Groups.Should().ContainSingle().Which.Count.Should().Be(2);

        await FluentActions.Awaiting(() => Orders(new ChildFilter("nope", FieldFilter.Equal("tag", "rush")))).Should().ThrowAsync<ArgumentException>();
        await FluentActions.Awaiting(() => Orders(new ChildFilter("tags", FieldFilter.Equal("nope", "rush")))).Should().ThrowAsync<InvalidQueryException>();
    }

    [Fact]
    public async Task A_none_child_filter_keeps_the_parents_without_a_child_row_the_condition_keeps()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        h.Hints.Declare(new FormTypeHints(Tag, Tag.Value, [new FieldHint("order_no", ColumnType.Text), new FieldHint("tag", ColumnType.Text)]));
        h.Hints.Declare(new FormTypeHints(Order, Order.Value,
            [new FieldHint("order_no", ColumnType.Text), new FieldHint("customer", ColumnType.Text)],
            [new RelationHint("lines", RelationKind.Child, Line, "order_no", "lines"), new RelationHint("tags", RelationKind.Child, Tag, "order_no", "tags")]));
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-1","customer":"Kim","lines":[{"product":"nut","qty":5}],"tags":["rush","fragile"]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-2","customer":"Kim","tags":["rush"]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-3","customer":"Lee","tags":[]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-4","customer":"Kim"}"""), cancellationToken: ct);
        foreach (var type in new[] { Order, Line, Tag })
        {
            await h.Engine.ProjectAsync(type, ct);
        }

        async Task<string?[]> Orders(params ChildFilter[] children) =>
            [.. (await h.Engine.QueryAsync(Order, new QuerySpec(OrderBy: [new OrderKey("order_no")], Children: children), ct)).Rows.Select(r => (string?)r.Fields["order_no"])];

        (await Orders(new ChildFilter("tags", Quantifier: ChildQuantifier.None))).Should().Equal(["A-3", "A-4"], "no row at all — an empty list and a missing one alike");
        (await Orders(new ChildFilter("tags"))).Should().Equal(["A-1", "A-2"], "without a condition, any row");
        (await Orders(new ChildFilter("tags", FieldFilter.Equal("tag", "fragile"), ChildQuantifier.None))).Should().Equal("A-2", "A-3", "A-4");
        (await Orders(new ChildFilter("tags", FieldFilter.Equal("tag", "rush")), new ChildFilter("tags", FieldFilter.Equal("tag", "fragile"), ChildQuantifier.None))).Should().Equal("A-2");
        (await Orders(new ChildFilter("lines", Quantifier: ChildQuantifier.None), new ChildFilter("tags", Quantifier: ChildQuantifier.None))).Should().Equal("A-3", "A-4");

        // The «none checked» group beside the per-option groups: the parents with no row, counted on the parent.
        var unchecked_ = await h.Engine.AggregateAsync(Order, new AggregateSpec(GroupBy: ["customer"], Children: [new ChildFilter("tags", Quantifier: ChildQuantifier.None)]), ct);
        unchecked_.Groups.Select(g => ((string?)g.Key["customer"], g.Count)).Should().BeEquivalentTo([("Kim", 1), ("Lee", 1)]);

        await FluentActions.Awaiting(() => Orders(new ChildFilter("tags", FieldFilter.Equal("nope", "rush"), ChildQuantifier.None))).Should().ThrowAsync<InvalidQueryException>();
    }

    [Fact]
    public async Task An_answer_that_reads_a_section_says_how_many_documents_its_projection_could_not_read()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        h.Hints.Declare(new FormTypeHints(Tag, Tag.Value, [new FieldHint("order_no", ColumnType.Text), new FieldHint("tag", ColumnType.Text)]));
        h.Hints.Declare(new FormTypeHints(Order, Order.Value,
            [new FieldHint("order_no", ColumnType.Text), new FieldHint("customer", ColumnType.Text)],
            [new RelationHint("tags", RelationKind.Child, Tag, "order_no", "tags")]));
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-1","customer":"Kim","tags":["rush"]}"""), cancellationToken: ct);
        await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-2","customer":"Kim","tags":[]}"""), cancellationToken: ct);
        // Written by something other than the form: a lone value where the section holds a list.
        var unreadable = await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-3","customer":"Lee","tags":"rush"}"""), cancellationToken: ct);
        await h.Engine.ProjectAsync(Order, ct);
        (await h.Engine.ProjectAsync(Tag, ct)).Skipped.Should().ContainSingle().Which.DocumentId.Should().Be(unreadable);

        var none = await h.Engine.QueryAsync(Order, new QuerySpec(OrderBy: [new OrderKey("order_no")], Children: [new ChildFilter("tags", Quantifier: ChildQuantifier.None)]), ct);
        none.Rows.Select(r => (string?)r.Fields["order_no"]).Should().Equal(["A-2", "A-3"], "the parent row stands; its section has no rows");
        none.SkippedDocuments.Should().Be(1, "A-3 is kept for having no tag only because its tags could not be read");

        var any = await h.Engine.QueryAsync(Order, new QuerySpec(Children: [new ChildFilter("tags", FieldFilter.Equal("tag", "rush"))]), ct);
        any.Rows.Should().ContainSingle();
        any.SkippedDocuments.Should().Be(1, "A-3's rush cannot be found");

        (await h.Engine.QueryAsync(Order, QuerySpec.All, ct)).SkippedDocuments.Should().Be(0, "an answer that does not read the section is over every document");
        (await h.Engine.AggregateAsync(Order, new AggregateSpec(Children: [new ChildFilter("tags", Quantifier: ChildQuantifier.None)]), ct))
            .SkippedDocuments.Should().Be(1);
        (await h.Engine.QueryAsync(Tag, QuerySpec.All, ct)).SkippedDocuments.Should().Be(1, "the section's own table is missing A-3's rows");
    }

    [Fact]
    public async Task A_value_in_a_section_of_objects_is_refused_by_naming_what_a_section_of_values_declares()
    {
        var store = CreateStore();
        var h = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        var document = await h.Engine.AcceptAsync(Order, DocumentBody.Parse("""{"order_no":"A-1","lines":["bolt"]}"""), cancellationToken: ct);

        var projected = await h.Engine.ProjectAsync(Line, ct);

        projected.Skipped.Should().ContainSingle().Which.Should().Match<ProjectionSkip>(s =>
            s.DocumentId == document && s.Reason.Contains("a section of values declares one field beside its key"));
    }
}

/// <summary>Runs the section contract against the in-memory store.</summary>
public sealed class InMemorySectionStoreContractTests : SectionStoreContractTests
{
    protected override IProjectionStore CreateStore() => new InMemoryProjectionStore();
}
