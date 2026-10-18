using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Contracts;

/// <summary>
/// A <see cref="FieldBinding.Reference"/> field reads its target record's value as it is now, and the
/// field is a declared field like any other: a query filters, orders and groups by it. Every
/// <see cref="IProjectionStore"/> answers the same.
/// <para>
/// A reference used to be left empty by projection — "resolution is a query-layer concern and is not
/// executed yet" — so a consumer rebuilt the join itself, reading the target and matching keys in
/// application code.
/// </para>
/// </summary>
public abstract class ReferenceResolutionContractTests
{
    protected abstract IProjectionStore CreateStore();

    // Unique per test instance so live stores sharing one database don't collide across tests.
    private readonly string _prefix = "r" + Guid.NewGuid().ToString("N")[..10];

    private FormTypeRef Customers => FormTypeRef.Create(_prefix + "_cust");

    private FormTypeRef Orders => FormTypeRef.Create(_prefix + "_ord");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// customers(code, grade) · orders(customer_ref, qty, grade_now) where <c>grade_now</c> is a reference
    /// to the customer's grade, found by <paramref name="lookup"/> through <c>customer_ref</c>.
    /// </summary>
    private (FormbaseEngine Engine, IProjectionStore Store) Engine(TargetLookup lookup)
    {
        var (engine, store, _) = EngineWithHints(lookup);
        return (engine, store);
    }

    private (FormbaseEngine Engine, IProjectionStore Store, InMemoryFieldHintSource Hints) EngineWithHints(TargetLookup lookup)
    {
        var store = CreateStore();
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var state = new InMemoryProjectionState();
        var proposer = new HintSchemaProposer(hints);
        var engine = new FormbaseEngine(
            new IntakeService(raw),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state,
            proposer);

        hints.Declare(new FormTypeHints(Customers, Customers.Value,
        [
            new FieldHint("code", ColumnType.Text),
            new FieldHint("grade", ColumnType.Text),
            new FieldHint("rate", ColumnType.Decimal),
        ]));
        hints.Declare(new FormTypeHints(Orders, Orders.Value,
        [
            new FieldHint("customer_ref", ColumnType.Text),
            new FieldHint("qty", ColumnType.Integer),
            new FieldHint("grade_now", ColumnType.Text, Binding: FieldBinding.Reference,
                Target: new EntityRef(Customers, "grade", lookup, "customer_ref")),
            new FieldHint("rate_now", ColumnType.Decimal, Binding: FieldBinding.Reference,
                Target: new EntityRef(Customers, "rate", lookup, "customer_ref")),
        ]));
        return (engine, store, hints);
    }

    private static Task<DocumentId> Accept(FormbaseEngine engine, FormTypeRef type, string json, RecordKey? key = null) =>
        engine.AcceptAsync(type, DocumentBody.Parse(json), recordKey: key, cancellationToken: Ct);

    private async Task ProjectBothAsync(FormbaseEngine engine)
    {
        await engine.ProjectAsync(Customers, Ct);
        await engine.ProjectAsync(Orders, Ct);
    }

    private async Task DropAsync(IProjectionStore store)
    {
        await store.DropTableAsync(Orders.Value, Ct);
        await store.DropTableAsync(Customers.Value, Ct);
    }

    private async Task<IReadOnlyList<object?>> GradesAsync(FormbaseEngine engine, QuerySpec? spec = null) =>
        (await engine.QueryAsync(Orders, spec ?? new QuerySpec(OrderBy: [new OrderKey("qty")]), Ct)).Rows
            .Select(r => r.Fields["grade_now"])
            .ToList();

    [Fact]
    public async Task A_reference_reads_its_targets_value_as_it_is_now()
    {
        var (engine, store) = Engine(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"A"}""", RecordKey.Create("cust-1"));
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await Accept(engine, Orders, """{"customer_ref":"C-9","qty":2}""");
        await Accept(engine, Orders, """{"qty":3}""");
        var projected = await engine.ProjectAsync(Orders, Ct);
        await engine.ProjectAsync(Customers, Ct);

        projected.UnresolvedReferences.Should().BeEmpty("a reference with a lookup is computed when it is read");
        (await GradesAsync(engine)).Should().Equal(new object?[] { "A", null, null });

        // The target is corrected: the reference reads the new value without re-projecting the orders.
        await Accept(engine, Customers, """{"code":"C-1","grade":"B"}""", RecordKey.Create("cust-1"));
        (await engine.QueryAsync(Orders, QuerySpec.All, Ct)).Stale.Should().BeTrue(
            "the target has a document its projection has not taken in, so the value read is not current");
        await engine.ProjectAsync(Customers, Ct);

        var now = await engine.QueryAsync(Orders, new QuerySpec(OrderBy: [new OrderKey("qty")]), Ct);
        now.Stale.Should().BeFalse();
        now.Rows.Select(r => r.Fields["grade_now"]).Should().Equal(new object?[] { "B", null, null });

        await DropAsync(store);
    }

    // A measure reads a reference as it reads any column: the target's value as it is now. An order
    // whose reference finds no customer takes no part, and a corrected rate reaches the sum without
    // re-projecting the orders.
    [Fact]
    public async Task An_aggregate_measures_a_reference_as_its_targets_value_now()
    {
        var (engine, store) = Engine(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"A","rate":1.5}""", RecordKey.Create("cust-1"));
        await Accept(engine, Customers, """{"code":"C-2","grade":"A","rate":2.25}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":2}""");
        await Accept(engine, Orders, """{"customer_ref":"C-2","qty":3}""");
        await Accept(engine, Orders, """{"customer_ref":"C-9","qty":4}""");
        await ProjectBothAsync(engine);

        var spec = new AggregateSpec(GroupBy: ["grade_now"], Measures: [AggregateMeasure.Sum("rate_now"), AggregateMeasure.Max("rate_now")]);
        var groups = (await engine.AggregateAsync(Orders, spec, Ct)).Groups;
        var a = groups.Single(g => Equals(g.Key["grade_now"], "A"));
        a.Count.Should().Be(3);
        a.Measures.Select(m => m.Value).Should().Equal(5.25m, 2.25m);
        a.Measures.Select(m => m.ValueCount).Should().AllBeEquivalentTo(3L);
        var orphan = groups.Single(g => g.Key["grade_now"] is null);
        orphan.Measures.Should().AllSatisfy(m => m.ValueCount.Should().Be(0, "the order whose customer is not there has no rate to add"));

        await Accept(engine, Customers, """{"code":"C-1","grade":"A","rate":2}""", RecordKey.Create("cust-1"));
        await engine.ProjectAsync(Customers, Ct);
        var corrected = (await engine.AggregateAsync(Orders, spec, Ct)).Groups.Single(g => Equals(g.Key["grade_now"], "A"));
        corrected.Measures[0].Value.Should().Be(6.25m, "the corrected rate is read, the orders not re-projected");

        await DropAsync(store);
    }

    private Task<QueryResult> SourcesAsync(FormbaseEngine engine) =>
        engine.QueryAsync(Orders, new QuerySpec(OrderBy: [new OrderKey("qty")], ReferenceSources: true), Ct);

    // Asked for, each reference says which target record its value is that record's field of — and,
    // when the value is empty, why: no target record, or a target record whose field is empty.
    [Fact]
    public async Task A_reference_says_which_target_record_its_value_was_read_from()
    {
        var (engine, store) = Engine(TargetLookup.Field("code"));
        var keyed = await Accept(engine, Customers, """{"code":"C-1","grade":"A"}""", RecordKey.Create("cust-1"));
        var own = await Accept(engine, Customers, """{"code":"C-2"}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await Accept(engine, Orders, """{"customer_ref":"C-2","qty":2}""");
        await Accept(engine, Orders, """{"customer_ref":"C-9","qty":3}""");
        await Accept(engine, Orders, """{"qty":4}""");
        await ProjectBothAsync(engine);

        var rows = (await SourcesAsync(engine)).Rows;

        rows.Select(r => r.Fields["grade_now"]).Should().Equal(new object?[] { "A", null, null, null });
        rows.Select(r => r.References!["grade_now"]).Should().Equal(
            new ReferenceSource(new RecordRef(keyed, RecordKey.Create("cust-1")), ReferenceOutcome.Resolved),
            new ReferenceSource(new RecordRef(own, null), ReferenceOutcome.Resolved),
            new ReferenceSource(null, ReferenceOutcome.NoTarget),
            new ReferenceSource(null, ReferenceOutcome.NoTarget));
        rows.Should().AllSatisfy(r => r.References!.Keys.Should().BeEquivalentTo(["grade_now", "rate_now"]));

        // A correction moves the record to its new document: the source follows the value.
        var corrected = await Accept(engine, Customers, """{"code":"C-1","grade":"B"}""", RecordKey.Create("cust-1"));
        await engine.ProjectAsync(Customers, Ct);
        var first = (await SourcesAsync(engine)).Rows[0];
        first.Fields["grade_now"].Should().Be("B");
        first.References!["grade_now"].Target.Should().Be(new RecordRef(corrected, RecordKey.Create("cust-1")));

        // Not asked for, nothing is read about the sources.
        (await GradesAsync(engine)).Should().HaveCount(4);
        (await engine.QueryAsync(Orders, QuerySpec.All, Ct)).Rows.Should().AllSatisfy(r => r.References.Should().BeNull());

        await DropAsync(store);
    }

    [Fact]
    public async Task When_several_target_records_match_the_source_is_the_one_the_value_was_read_from()
    {
        var (engine, store) = Engine(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"old"}""");
        var latest = await Accept(engine, Customers, """{"code":"C-1","grade":"new"}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await ProjectBothAsync(engine);

        var row = (await SourcesAsync(engine)).Rows.Single();
        row.Fields["grade_now"].Should().Be("new");
        row.References!["grade_now"].Should().Be(new ReferenceSource(new RecordRef(latest, null), ReferenceOutcome.Resolved));

        await DropAsync(store);
    }

    [Fact]
    public async Task A_record_lookup_says_which_record_it_found()
    {
        var (engine, store) = Engine(TargetLookup.Record);
        var keyed = await Accept(engine, Customers, """{"code":"C-1","grade":"A"}""", RecordKey.Create("cust-1"));
        await Accept(engine, Orders, """{"customer_ref":"cust-1","qty":1}""");
        await Accept(engine, Orders, """{"customer_ref":"cust-9","qty":2}""");
        await ProjectBothAsync(engine);

        (await SourcesAsync(engine)).Rows.Select(r => r.References!["grade_now"]).Should().Equal(
            new ReferenceSource(new RecordRef(keyed, RecordKey.Create("cust-1")), ReferenceOutcome.Resolved),
            new ReferenceSource(null, ReferenceOutcome.NoTarget));

        await DropAsync(store);
    }

    // A target with no projection, or one not yet rebuilt into the shape the reference reads, cannot
    // answer: that is not "no such record", and the source says which.
    [Fact]
    public async Task A_target_that_cannot_answer_is_told_apart_from_no_target_record()
    {
        var (engine, store, hints) = EngineWithHints(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"A"}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await engine.ProjectAsync(Orders, Ct);

        (await SourcesAsync(engine)).Rows.Single().References!["grade_now"]
            .Should().Be(new ReferenceSource(null, ReferenceOutcome.TargetUnavailable), "the customers were never projected");

        await engine.ProjectAsync(Customers, Ct);
        hints.Declare(new FormTypeHints(Customers, Customers.Value,
        [
            new FieldHint("code", ColumnType.Text),
            new FieldHint("grade", ColumnType.Text),
            new FieldHint("tier", ColumnType.Text),
        ]));
        hints.Declare(new FormTypeHints(Orders, Orders.Value,
        [
            new FieldHint("customer_ref", ColumnType.Text),
            new FieldHint("qty", ColumnType.Integer),
            new FieldHint("grade_now", ColumnType.Text, Binding: FieldBinding.Reference,
                Target: new EntityRef(Customers, "tier", TargetLookup.Field("code"), "customer_ref")),
        ]));
        await engine.ProjectAsync(Orders, Ct);

        (await SourcesAsync(engine)).Rows.Single().References!["grade_now"]
            .Should().Be(new ReferenceSource(null, ReferenceOutcome.TargetUnavailable), "the customers have no tier column yet");

        await DropAsync(store);
    }

    [Fact]
    public async Task When_several_target_records_match_the_latest_accepted_is_read()
    {
        var (engine, store) = Engine(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"old"}""");
        await Accept(engine, Customers, """{"code":"C-1","grade":"new"}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await ProjectBothAsync(engine);

        (await GradesAsync(engine)).Should().Equal(["new"], "two records share the code; one row is read, not two");
        (await engine.QueryAsync(Orders, QuerySpec.All, Ct)).Rows.Should().ContainSingle();

        await DropAsync(store);
    }

    [Fact]
    public async Task A_reference_filters_orders_and_groups_like_any_declared_field()
    {
        var (engine, store) = Engine(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"A"}""");
        await Accept(engine, Customers, """{"code":"C-2","grade":"B"}""");
        await Accept(engine, Orders, """{"customer_ref":"C-2","qty":1}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":2}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":3}""");
        await ProjectBothAsync(engine);

        var filtered = await engine.QueryAsync(Orders, new QuerySpec(Filters: [new FieldFilter("grade_now", FilterOperator.Equal, "A")], OrderBy: [new OrderKey("qty")]), Ct);
        filtered.Rows.Select(r => r.Fields["qty"]).Should().Equal(2L, 3L);

        (await GradesAsync(engine, new QuerySpec(OrderBy: [new OrderKey("grade_now", Descending: true), new OrderKey("qty")])))
            .Should().Equal("B", "A", "A");

        var groups = (await engine.AggregateAsync(Orders, new AggregateSpec(GroupBy: ["grade_now"]), Ct)).Groups;
        groups.Select(g => (g.Key["grade_now"], g.Count)).Should().Equal(("A", 2L), ("B", 1L));

        await DropAsync(store);
    }

    [Fact]
    public async Task A_lookup_by_record_finds_a_keyed_record_by_its_key_and_a_keyless_one_by_its_document()
    {
        var (engine, store) = Engine(TargetLookup.Record);
        await Accept(engine, Customers, """{"code":"C-1","grade":"keyed"}""", RecordKey.Create("cust-1"));
        var keyless = await Accept(engine, Customers, """{"code":"C-2","grade":"keyless"}""");
        await Accept(engine, Orders, """{"customer_ref":"cust-1","qty":1}""");
        await Accept(engine, Orders, $$"""{"customer_ref":"{{keyless.Value}}","qty":2}""");
        await ProjectBothAsync(engine);

        (await GradesAsync(engine)).Should().Equal("keyed", "keyless");
        (await engine.QueryAsync(Orders, new QuerySpec(Filters: [new FieldFilter("grade_now", FilterOperator.Equal, "keyless")]), Ct))
            .Rows.Single().Fields["qty"].Should().Be(2L, "a reference found by record filters like any declared field");
        (await GradesAsync(engine, new QuerySpec(OrderBy: [new OrderKey("grade_now", Descending: true)])))
            .Should().Equal("keyless", "keyed");

        // A correction keeps the record key, so the reference follows the record to its new document.
        await Accept(engine, Customers, """{"code":"C-1","grade":"corrected"}""", RecordKey.Create("cust-1"));
        await engine.ProjectAsync(Customers, Ct);
        (await GradesAsync(engine)).Should().Equal("corrected", "keyless");

        await DropAsync(store);
    }

    [Fact]
    public async Task A_target_that_was_never_projected_reads_null_and_the_result_is_stale()
    {
        var (engine, store) = Engine(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"A"}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await engine.ProjectAsync(Orders, Ct);

        var result = await engine.QueryAsync(Orders, QuerySpec.All, Ct);
        result.Rows.Single().Fields["grade_now"].Should().BeNull();
        result.Stale.Should().BeTrue("a reference into a projection that does not exist is not current");

        await store.DropTableAsync(Orders.Value, Ct);
    }

    /// <summary>
    /// The target is redeclared with the field the reference now reads, and the source is rebuilt
    /// first — projections run in no particular order. Until the target is rebuilt its table has no
    /// such column: the reference reads null and the result is stale, as for a target never projected,
    /// rather than the read failing.
    /// </summary>
    [Fact]
    public async Task A_target_not_yet_rebuilt_into_its_redeclared_shape_reads_null_and_the_result_is_stale()
    {
        var (engine, store, hints) = EngineWithHints(TargetLookup.Field("code"));
        await Accept(engine, Customers, """{"code":"C-1","grade":"A","tier":"gold"}""");
        await Accept(engine, Orders, """{"customer_ref":"C-1","qty":1}""");
        await ProjectBothAsync(engine);

        hints.Declare(new FormTypeHints(Customers, Customers.Value,
        [
            new FieldHint("code", ColumnType.Text),
            new FieldHint("grade", ColumnType.Text),
            new FieldHint("tier", ColumnType.Text),
        ]));
        hints.Declare(new FormTypeHints(Orders, Orders.Value,
        [
            new FieldHint("customer_ref", ColumnType.Text),
            new FieldHint("qty", ColumnType.Integer),
            new FieldHint("grade_now", ColumnType.Text, Binding: FieldBinding.Reference,
                Target: new EntityRef(Customers, "tier", TargetLookup.Field("code"), "customer_ref")),
        ]));
        await engine.ProjectAsync(Orders, Ct);

        var before = await engine.QueryAsync(Orders, QuerySpec.All, Ct);
        before.Rows.Single().Fields["grade_now"].Should().BeNull();
        before.Stale.Should().BeTrue("the target's projection is not in its declared shape yet");

        await engine.ProjectAsync(Customers, Ct);
        (await GradesAsync(engine)).Should().Equal(["gold"]);

        await DropAsync(store);
    }

    /// <summary>
    /// A target projected before rows carried their record identity column — the table an upgrade
    /// finds — cannot answer a lookup by record until it is rebuilt: the reference reads null.
    /// </summary>
    [Fact]
    public async Task A_record_lookup_into_a_table_built_without_the_record_column_reads_null()
    {
        var (engine, store) = Engine(TargetLookup.Record);
        await store.CreateTableAsync(new TableSchema(Customers.Value,
        [
            new ColumnDef(ProjectionSystemColumns.DocumentId, ColumnType.Uuid, Nullable: false),
            new ColumnDef(ProjectionSystemColumns.Watermark, ColumnType.Integer, Nullable: false),
            new ColumnDef(ProjectionSystemColumns.RecordKey, ColumnType.Text, Nullable: true),
            new ColumnDef("code", ColumnType.Text),
            new ColumnDef("grade", ColumnType.Text),
        ]), Ct);
        await store.BulkInsertAsync(Customers.Value,
        [
            new Dictionary<string, object?>
            {
                [ProjectionSystemColumns.DocumentId] = Guid.NewGuid(),
                [ProjectionSystemColumns.Watermark] = 1L,
                [ProjectionSystemColumns.RecordKey] = "cust-1",
                ["code"] = "C-1",
                ["grade"] = "old layout",
            },
        ], Ct);
        await Accept(engine, Orders, """{"customer_ref":"cust-1","qty":1}""");
        await engine.ProjectAsync(Orders, Ct);

        (await GradesAsync(engine)).Should().Equal([null]);

        await DropAsync(store);
    }
}

/// <summary>Runs the reference-resolution contract against the in-memory store.</summary>
public sealed class InMemoryReferenceResolutionContractTests : ReferenceResolutionContractTests
{
    protected override IProjectionStore CreateStore() => new InMemoryProjectionStore();
}
