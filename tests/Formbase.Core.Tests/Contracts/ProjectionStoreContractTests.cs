using System.Globalization;
using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Contracts;

/// <summary>
/// The behavioral contract every <see cref="IProjectionStore"/> must honor. Held implementation-agnostic:
/// the exact exception types differ (in-memory throws <see cref="InvalidOperationException"/>, a real
/// MorphDB adapter throws its own conflict/not-found types), so the throwing cases assert only that some
/// error surfaces. When a query carries no ordering, row order is unspecified (so those assertions stay
/// order-independent); when it carries a <see cref="OrderKey"/>, the store must return rows in that total
/// order, which is what makes offset/limit paging deterministic.
/// </summary>
public abstract class ProjectionStoreContractTests
{
    protected abstract IProjectionStore CreateStore();

    // Unique per test instance so live stores sharing one database don't collide across tests.
    protected string TableName { get; } = "t_" + Guid.NewGuid().ToString("N");

    private TableSchema Schema() =>
        new(TableName, [new ColumnDef("k", ColumnType.Text), new ColumnDef("v", ColumnType.Integer)]);

    private static IReadOnlyDictionary<string, object?> Row(string k, long v) =>
        new Dictionary<string, object?> { ["k"] = k, ["v"] = v };

    [Fact]
    public async Task Create_then_exists_is_true_and_drop_makes_it_false()
    {
        var store = CreateStore();

        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        (await store.TableExistsAsync(TableName, TestContext.Current.CancellationToken)).Should().BeTrue();

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
        (await store.TableExistsAsync(TableName, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Drop_is_idempotent()
    {
        var store = CreateStore();

        var act = () => store.DropTableAsync(TableName);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Create_fails_when_the_table_already_exists()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);

        var act = () => store.CreateTableAsync(Schema());

        await act.Should().ThrowAsync<Exception>();
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Insert_into_a_missing_table_fails()
    {
        var store = CreateStore();

        var act = () => store.BulkInsertAsync(TableName, [Row("a", 1)]);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Query_returns_inserted_rows()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2)], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, QuerySpec.All, TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_applies_equality_filters()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("a", 3)], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, new QuerySpec(
            Filters: [FieldFilter.Equal("k", "a")]), TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => Equals(r["k"], "a"));
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_limit_caps_the_row_count()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("c", 3), Row("d", 4)], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, new QuerySpec(Limit: 2), TestContext.Current.CancellationToken);

        rows.Should().HaveCount(2);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_orders_by_the_requested_key_in_both_directions()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        // Inserted out of order, so passing order (not insertion order) is what's being verified.
        await store.BulkInsertAsync(TableName, [Row("a", 3), Row("b", 1), Row("c", 2)], TestContext.Current.CancellationToken);

        var asc = await store.QueryAsync(TableName, new QuerySpec(OrderBy: [new OrderKey("v")]), TestContext.Current.CancellationToken);
        asc.Select(r => Convert.ToInt64(r["v"], CultureInfo.InvariantCulture)).Should().ContainInOrder(1L, 2L, 3L);

        var desc = await store.QueryAsync(TableName, new QuerySpec(OrderBy: [new OrderKey("v", Descending: true)]), TestContext.Current.CancellationToken);
        desc.Select(r => Convert.ToInt64(r["v"], CultureInfo.InvariantCulture)).Should().ContainInOrder(3L, 2L, 1L);

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ordered_paging_returns_a_deterministic_slice()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 3), Row("b", 1), Row("d", 4), Row("c", 2)], TestContext.Current.CancellationToken);

        // Ordered by v ascending → 1,2,3,4; skip 1, take 2 → the middle slice, same on any store.
        var page = await store.QueryAsync(TableName, new QuerySpec(Limit: 2, Offset: 1, OrderBy: [new OrderKey("v")]), TestContext.Current.CancellationToken);

        page.Select(r => Convert.ToInt64(r["v"], CultureInfo.InvariantCulture)).Should().ContainInOrder(2L, 3L);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    private static IReadOnlyDictionary<string, object?> RowWithNull(string k) =>
        new Dictionary<string, object?> { ["k"] = k, ["v"] = null };

    private static long[] Values(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        rows.Select(r => Convert.ToInt64(r["v"], CultureInfo.InvariantCulture)).Order().ToArray();

    [Theory]
    [InlineData(FilterOperator.GreaterThan, 2L, new[] { 3L, 4L })]
    [InlineData(FilterOperator.GreaterThanOrEqual, 2L, new[] { 2L, 3L, 4L })]
    [InlineData(FilterOperator.LessThan, 3L, new[] { 1L, 2L })]
    [InlineData(FilterOperator.LessThanOrEqual, 3L, new[] { 1L, 2L, 3L })]
    public async Task Query_applies_range_filters_and_a_null_column_matches_none(FilterOperator op, long bound, long[] expected)
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("c", 3), Row("d", 4), RowWithNull("e")], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, new QuerySpec(Filters: [new FieldFilter("v", op, bound)]), TestContext.Current.CancellationToken);

        Values(rows).Should().Equal(expected);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_selects_empty_columns_by_isnull_and_by_equality_with_null()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), RowWithNull("b"), Row("c", 3), RowWithNull("d")], TestContext.Current.CancellationToken);

        var empty = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.IsNull("v")]), TestContext.Current.CancellationToken);
        empty.Select(r => (string)r["k"]!).Should().BeEquivalentTo(["b", "d"]);

        var equalNull = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.Equal("v", null)]), TestContext.Current.CancellationToken);
        equalNull.Select(r => (string)r["k"]!).Should().BeEquivalentTo(["b", "d"], "equality with null is the same question");

        var filled = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.IsNotNull("v")]), TestContext.Current.CancellationToken);
        Values(filled).Should().Equal(1L, 3L);

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Isnull_combines_with_other_filters_and_counts_in_aggregates()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), RowWithNull("a"), RowWithNull("b"), Row("b", 2)], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.IsNull("v"), FieldFilter.Equal("k", "a")]), TestContext.Current.CancellationToken);
        rows.Should().ContainSingle().Which["k"].Should().Be("a");

        var groups = await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["k"], Filters: [FieldFilter.IsNotNull("v")]), TestContext.Current.CancellationToken);
        groups.ToDictionary(g => (string)g.Key["k"]!, g => g.Count)
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 1, ["b"] = 1 });

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_combines_filters_as_and()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("c", 3), Row("d", 4)], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, new QuerySpec(Filters:
        [
            new FieldFilter("v", FilterOperator.GreaterThanOrEqual, 2L),
            new FieldFilter("v", FilterOperator.LessThan, 4L),
        ]), TestContext.Current.CancellationToken);

        Values(rows).Should().Equal(2L, 3L);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Query_matches_text_by_contains_and_prefix_ignoring_case()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("Pump-Alpha", 1), Row("pump-beta", 2), Row("Valve-PUMP", 3), Row("Motor", 4)], TestContext.Current.CancellationToken);

        var containing = await store.QueryAsync(TableName, new QuerySpec(Filters: [new FieldFilter("k", FilterOperator.Contains, "pump")]), TestContext.Current.CancellationToken);
        Values(containing).Should().Equal(1L, 2L, 3L);

        var prefixed = await store.QueryAsync(TableName, new QuerySpec(Filters: [new FieldFilter("k", FilterOperator.StartsWith, "PUMP")]), TestContext.Current.CancellationToken);
        Values(prefixed).Should().Equal(1L, 2L);

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    // In is equality against each listed value: text exact (not case-folded, as Equal is not), numbers by
    // value, an empty column never listed; an empty list keeps nothing. Aggregates read the same filter.
    [Fact]
    public async Task In_keeps_the_rows_whose_value_is_listed_in_queries_and_aggregates()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateTableAsync(Schema(), ct);
        await store.BulkInsertAsync(TableName, [Row("a,b", 1), Row("c", 2), Row("C", 3), Row("d", 4)], ct);
        await store.BulkInsertAsync(TableName, [new Dictionary<string, object?> { ["k"] = null, ["v"] = 5L }], ct);

        var byText = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.In("k", ["a,b", "c", "zz"])]), ct);
        Values(byText).Should().Equal(1L, 2L);

        var byNumber = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.In("v", [4L, 5L]), FieldFilter.IsNotNull("k")]), ct);
        Values(byNumber).Should().Equal(4L);

        var none = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.In("k", [])]), ct);
        none.Should().BeEmpty();

        var counted = await store.AggregateAsync(TableName, new AggregateSpec(Filters: [FieldFilter.In("v", [1L, 3L, 5L])]), ct);
        counted.Should().ContainSingle().Which.Count.Should().Be(3);

        await store.DropTableAsync(TableName, ct);
    }

    // Through the engine an In list is coerced to the column's type element by element, and a list with an
    // empty value in it is refused: IsNull is how an empty column is asked for.
    [Fact]
    public async Task In_through_the_engine_coerces_each_value_and_refuses_an_empty_one()
    {
        var store = CreateStore();
        var (engine, type) = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        foreach (var (lot, qty) in new[] { ("A", 1), ("B", 2), ("C", 3) })
        {
            await engine.AcceptAsync(type, DocumentBody.Parse($$"""{"lot":"{{lot}}","qty":{{qty}}}"""), cancellationToken: ct);
        }

        await engine.ProjectAsync(type, ct);

        var rows = await engine.QueryAsync(type, new QuerySpec(Filters: [FieldFilter.In("qty", [1, 3])]), ct);
        rows.Rows.Select(r => r.Fields["lot"]).Should().BeEquivalentTo(["A", "C"], "an int listed against an integer column is the same number");

        var refused = () => engine.QueryAsync(type, new QuerySpec(Filters: [new FieldFilter("lot", FilterOperator.In, new object?[] { "A", null })]), ct);
        await refused.Should().ThrowAsync<InvalidQueryException>();

        await store.DropTableAsync(TableName, ct);
    }

    [Fact]
    public async Task Aggregate_counts_one_group_per_distinct_key_null_included()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("a", 3), RowWithNull("c"), RowWithNull("d")], TestContext.Current.CancellationToken);

        var byK = await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["k"]), TestContext.Current.CancellationToken);
        byK.ToDictionary(g => (string)g.Key["k"]!, g => g.Count)
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 2, ["b"] = 1, ["c"] = 1, ["d"] = 1 });

        var byV = await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["v"]), TestContext.Current.CancellationToken);
        byV.Should().HaveCount(4, "1, 2, 3 and one group for the rows whose v is null");
        byV.Single(g => g.Key["v"] is null).Count.Should().Be(2);

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Aggregate_counts_only_the_rows_its_filters_keep()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("a", 3), Row("a", 4)], TestContext.Current.CancellationToken);

        var groups = await store.AggregateAsync(TableName, new AggregateSpec(
            GroupBy: ["k"],
            Filters: [new FieldFilter("v", FilterOperator.GreaterThanOrEqual, 2L)]), TestContext.Current.CancellationToken);

        groups.ToDictionary(g => (string)g.Key["k"]!, g => g.Count)
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["a"] = 2, ["b"] = 1 });
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Ungrouped_aggregate_is_one_count_even_when_nothing_is_kept()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2)], TestContext.Current.CancellationToken);

        var all = await store.AggregateAsync(TableName, AggregateSpec.CountAll, TestContext.Current.CancellationToken);
        all.Should().ContainSingle().Which.Count.Should().Be(2);

        var none = await store.AggregateAsync(TableName, new AggregateSpec(Filters: [FieldFilter.Equal("k", "zzz")]), TestContext.Current.CancellationToken);
        none.Should().ContainSingle().Which.Count.Should().Be(0);

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    // The records behind each group come from the same read as its count, in the order they were
    // accepted -- not in the order of their ids, which differs between stores -- cut at the limit. The
    // ids below are chosen so that id order and acceptance order disagree, and keyed and keyless
    // documents are interleaved within a group so that a key read apart from its document (a null
    // dropped, or the two read in different orders) pairs a document with the wrong key.
    [Fact]
    public async Task Aggregate_carries_each_groups_records_in_acceptance_order_up_to_the_limit()
    {
        var store = CreateStore();
        var schema = new TableSchema(TableName, [.. ProjectionSystemColumns.All, new ColumnDef("k", ColumnType.Text)]);
        await store.CreateTableAsync(schema, TestContext.Current.CancellationToken);
        Guid Id(char c) => Guid.Parse(new string(c, 8) + "-0000-7000-8000-000000000000");
        IReadOnlyDictionary<string, object?> Doc(char id, long watermark, string? key, string? k) => new Dictionary<string, object?>
        {
            [ProjectionSystemColumns.DocumentId] = Id(id),
            [ProjectionSystemColumns.Watermark] = watermark,
            [ProjectionSystemColumns.RecordKey] = key,
            [ProjectionSystemColumns.Record] = ProjectionSystemColumns.RecordOf(DocumentId.From(Id(id)), key is null ? null : RecordKey.Create(key)),
            ["k"] = k,
        };
        RecordRef Ref(char id, string? key) => new(DocumentId.From(Id(id)), key is null ? null : RecordKey.Create(key));
        await store.BulkInsertAsync(TableName,
            [Doc('1', 3, null, "a"), Doc('f', 1, "kf", "a"), Doc('2', 2, null, "b"), Doc('0', 5, "k0", "a"), Doc('9', 4, "k9", null)],
            TestContext.Current.CancellationToken);

        var grouped = await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["k"], RecordsPerGroup: 2), TestContext.Current.CancellationToken);

        var a = grouped.Single(g => Equals(g.Key["k"], "a"));
        a.Count.Should().Be(3);
        a.Records.Should().Equal(Ref('f', "kf"), Ref('1', null));
        grouped.Single(g => Equals(g.Key["k"], "b")).Records.Should().Equal(Ref('2', null));
        grouped.Single(g => g.Key["k"] is null).Records.Should().Equal(Ref('9', "k9"));

        var whole = await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["k"], RecordsPerGroup: 3), TestContext.Current.CancellationToken);
        whole.Single(g => Equals(g.Key["k"], "a")).Records.Should().Equal(
            [Ref('f', "kf"), Ref('1', null), Ref('0', "k0")], "a key after a keyless document keeps its own document");

        var all = await store.AggregateAsync(TableName, new AggregateSpec(RecordsPerGroup: 3), TestContext.Current.CancellationToken);
        all.Should().ContainSingle().Which.Records.Should().Equal(Ref('f', "kf"), Ref('2', null), Ref('1', null));

        var none = await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["k"]), TestContext.Current.CancellationToken);
        none.Should().OnlyContain(g => g.Records == null, "records are read only when asked for");

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    /// <summary>
    /// The engine's read path over the store under test: raw, declarations and projection state in
    /// memory, the projected table in the store. Which record a row is comes from the store's system
    /// columns, so this is where a store that returned them in a shape the core cannot read would show.
    /// </summary>
    private (FormbaseEngine Engine, FormTypeRef Type) Engine(IProjectionStore store)
    {
        var type = FormTypeRef.Create(TableName);
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var state = new InMemoryProjectionState();
        var proposer = new HintSchemaProposer(hints);
        var engine = new FormbaseEngine(
            new IntakeService(raw, hints),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state,
            proposer);
        hints.Declare(new FormTypeHints(type, TableName,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer),
        ]));
        return (engine, type);
    }

    // A keyed record keeps its key through a correction and moves to the correcting document; a
    // document without a key is a record of its own, named by its document alone. Both projection
    // paths are crossed: the first run builds the table, the second brings it forward.
    [Fact]
    public async Task A_query_row_says_which_record_it_is_across_a_correction()
    {
        var store = CreateStore();
        var (engine, type) = Engine(store);
        var key = RecordKey.Create("rec-1");
        var ct = TestContext.Current.CancellationToken;

        var standalone = await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":1}"""), cancellationToken: ct);
        var original = await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":2}"""), recordKey: key, cancellationToken: ct);
        await engine.ProjectAsync(type, ct);

        var before = await engine.QueryAsync(type, QuerySpec.All, ct);
        before.Rows.Select(r => r.Record).Should().Equal(new RecordRef(standalone, null), new RecordRef(original, key));
        before.Rows.Select(r => r.Fields["qty"]).Should().Equal(1L, 2L);
        before.Rows.Should().AllSatisfy(r => r.Fields.Keys.Should().BeEquivalentTo(["lot", "qty"],
            "the identity is carried beside the fields, not among them"));

        var corrected = await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":5}"""), recordKey: key, cancellationToken: ct);
        await engine.ProjectAsync(type, ct);

        var after = await engine.QueryAsync(type, QuerySpec.All, ct);
        after.Rows.Select(r => r.Record).Should().Equal(
            [new RecordRef(standalone, null), new RecordRef(corrected, key)],
            "the corrected record is still the record named rec-1, now standing on the correcting document");
        after.Rows.Select(r => r.Fields["qty"]).Should().Equal(1L, 5L);

        await store.DropTableAsync(TableName, ct);
    }

    // The evidence an aggregate gives for a count names the same records the group's own query reads,
    // in the same order — so a count can be matched to its rows — and stops at the limit.
    [Fact]
    public async Task An_aggregates_records_are_the_records_its_groups_query_reads()
    {
        var store = CreateStore();
        var (engine, type) = Engine(store);
        var ct = TestContext.Current.CancellationToken;

        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":1}"""), cancellationToken: ct);
        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":2}"""), recordKey: RecordKey.Create("rec-1"), cancellationToken: ct);
        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"B","qty":3}"""), cancellationToken: ct);
        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":4}"""), recordKey: RecordKey.Create("rec-2"), cancellationToken: ct);
        await engine.ProjectAsync(type, ct);
        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":6}"""), recordKey: RecordKey.Create("rec-1"), cancellationToken: ct);
        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":7}"""), cancellationToken: ct);
        await engine.ProjectAsync(type, ct);

        var spec = new AggregateSpec(GroupBy: ["lot"], RecordsPerGroup: 10);
        var groups = (await engine.AggregateAsync(type, spec, ct)).Groups;

        groups.Should().HaveCount(2);
        foreach (var group in groups)
        {
            var rows = await engine.QueryAsync(type, spec.RecordsOf(group), ct);
            group.Records.Should().Equal(rows.Rows.Select(r => r.Record), "group {0}'s evidence is its rows", group.Key["lot"]);
            group.Count.Should().Be(rows.Rows.Count);
        }

        var a = groups.Single(g => Equals(g.Key["lot"], "A"));
        a.Records!.Select(r => r.Key?.Value).Should().Equal([null, "rec-2", "rec-1", null],
            "in the order their current documents were accepted — the corrected record after the one accepted before its correction");

        var cut = (await engine.AggregateAsync(type, spec with { RecordsPerGroup = 2 }, ct)).Groups.Single(g => Equals(g.Key["lot"], "A"));
        cut.Count.Should().Be(4, "the limit cuts the list, not the count");
        cut.Records.Should().Equal(a.Records!.Take(2));

        await store.DropTableAsync(TableName, ct);
    }

    private TableSchema MeasuredSchema() =>
        new(TableName,
        [
            new ColumnDef("k", ColumnType.Text),
            new ColumnDef("n", ColumnType.Integer),
            new ColumnDef("d", ColumnType.Decimal),
            new ColumnDef("t", ColumnType.Timestamp),
        ]);

    private static IReadOnlyDictionary<string, object?> MeasuredRow(string k, long? n, decimal? d, string? t) =>
        new Dictionary<string, object?>
        {
            ["k"] = k,
            ["n"] = n,
            ["d"] = d,
            ["t"] = t is null ? null : DateTimeOffset.Parse(t, CultureInfo.InvariantCulture),
        };

    // A store answers with the number type its backend computes in; the engine types it (the test after
    // this one). Here only the values and the counts are the contract.
    private static decimal? Number(object? value) => value is null ? null : Convert.ToDecimal(value, CultureInfo.InvariantCulture);

    private static DateTimeOffset? Instant(object? value) => value switch
    {
        null => null,
        DateTimeOffset instant => instant,
        DateTime instant => new DateTimeOffset(DateTime.SpecifyKind(instant, DateTimeKind.Utc)),
        _ => DateTimeOffset.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture),
    };

    // Each measure is computed over the values the group has, and says how many that was: the row with
    // nothing in the columns is counted in the group but takes no part. Decimals are chosen so that
    // their text order (10.5 before 9.25) and their numeric order disagree, and the sum of a decimal
    // column is exact (0.1 + 0.2 is 0.3, not 0.30000000000000004).
    [Fact]
    public async Task Aggregate_measures_each_group_over_the_values_it_has()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateTableAsync(MeasuredSchema(), ct);
        await store.BulkInsertAsync(TableName,
        [
            MeasuredRow("a", 1, 10.5m, "2026-01-02T00:00:00Z"),
            MeasuredRow("a", 3, 9.25m, "2026-01-01T00:00:00Z"),
            MeasuredRow("a", null, null, null),
            MeasuredRow("b", 5, 0.1m, "2026-03-01T00:00:00Z"),
            MeasuredRow("b", 7, 0.2m, "2026-03-01T00:00:00Z"),
            MeasuredRow("c", null, null, null),
        ], ct);

        AggregateMeasure[] measures =
        [
            AggregateMeasure.Sum("n"), AggregateMeasure.Average("n"), AggregateMeasure.Sum("d"),
            AggregateMeasure.Min("d"), AggregateMeasure.Max("d"), AggregateMeasure.Min("t"), AggregateMeasure.Max("t"),
        ];
        var groups = (await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["k"], Measures: measures), ct))
            .ToDictionary(g => (string)g.Key["k"]!);

        var a = groups["a"];
        a.Count.Should().Be(3);
        a.Measures.Select(m => m.Measure).Should().Equal(measures, "one value per measure, in the order asked");
        a.Measures.Select(m => m.ValueCount).Should().AllBeEquivalentTo(2L, "the empty row takes no part");
        a.Measures.Take(5).Select(m => Number(m.Value)).Should().Equal(4m, 2m, 19.75m, 9.25m, 10.5m);
        a.Measures.Skip(5).Select(m => Instant(m.Value)).Should().Equal(
            DateTimeOffset.Parse("2026-01-01T00:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-01-02T00:00:00Z", CultureInfo.InvariantCulture));

        var b = groups["b"];
        Number(b.Measures[0].Value).Should().Be(12m);
        Number(b.Measures[1].Value).Should().Be(6m);
        Number(b.Measures[2].Value).Should().Be(0.3m, "a decimal sum is exact");

        var c = groups["c"];
        c.Count.Should().Be(1);
        c.Measures.Should().AllSatisfy(m =>
        {
            m.Value.Should().BeNull("a group with no values has no sum — not a zero");
            m.ValueCount.Should().Be(0);
        });

        var none = await store.AggregateAsync(TableName, new AggregateSpec(Filters: [FieldFilter.Equal("k", "zzz")], Measures: [AggregateMeasure.Sum("n")]), ct);
        none.Should().ContainSingle().Which.Measures.Should().ContainSingle()
            .Which.Should().Be(new MeasureValue(AggregateMeasure.Sum("n"), null, 0));

        await store.DropTableAsync(TableName, ct);
    }

    // A distinct count tells values apart the way an equality filter does: one decimal spelt two ways is one
    // value, one instant at two offsets is one value. Empty values are not counted, and a group with none
    // counts zero rather than nothing.
    [Fact]
    public async Task Aggregate_counts_the_distinct_values_each_group_has()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateTableAsync(MeasuredSchema(), ct);
        await store.BulkInsertAsync(TableName,
        [
            MeasuredRow("a", 1, 10.5m, "2026-01-02T00:00:00Z"),
            MeasuredRow("a", 1, 10.50m, "2026-01-02T09:00:00+09:00"),
            MeasuredRow("a", 2, 3m, null),
            MeasuredRow("a", null, null, null),
            MeasuredRow("c", null, null, null),
        ], ct);

        AggregateMeasure[] measures = [AggregateMeasure.CountDistinct("n"), AggregateMeasure.CountDistinct("d"), AggregateMeasure.CountDistinct("t"), AggregateMeasure.CountDistinct("k")];
        var groups = (await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["k"], Measures: measures), ct))
            .ToDictionary(g => (string)g.Key["k"]!);

        groups["a"].Measures.Select(m => Number(m.Value)).Should().Equal(2m, 2m, 1m, 1m);
        groups["a"].Measures.Select(m => m.ValueCount).Should().Equal(3L, 3L, 2L, 4L);
        groups["c"].Measures.Select(m => Number(m.Value)).Should().Equal(0m, 0m, 0m, 1m);

        await store.DropTableAsync(TableName, ct);
    }

    // Groups ranked by count or by a measure, ties broken by their key, and cut to the first N: the order
    // and the cut are the engine's, so every store gives the same top N.
    [Fact]
    public async Task An_aggregates_groups_are_ranked_and_cut_to_the_top()
    {
        var store = CreateStore();
        var (engine, type) = Engine(store);
        var ct = TestContext.Current.CancellationToken;
        foreach (var (lot, qty) in new[] { ("A", 1), ("A", 2), ("A", 1), ("B", 1), ("C", 3), ("C", 4), ("C", 5), ("D", 9) })
        {
            await engine.AcceptAsync(type, DocumentBody.Parse($$"""{"lot":"{{lot}}","qty":{{qty}}}"""), cancellationToken: ct);
        }

        await engine.ProjectAsync(type, ct);

        var byCount = await engine.AggregateAsync(type, new AggregateSpec(
            GroupBy: ["lot"], OrderBy: [GroupOrder.ByCount(descending: true)], Limit: 3), ct);
        // A and C tie on count and fall back to key order; the cut keeps three.
        byCount.Groups.Select(g => $"{g.Key["lot"]}:{g.Count}").Should().Equal("A:3", "C:3", "B:1");

        var byUsers = await engine.AggregateAsync(type, new AggregateSpec(
            GroupBy: ["lot"], Measures: [AggregateMeasure.CountDistinct("qty")], OrderBy: [GroupOrder.ByMeasure(0, descending: true), GroupOrder.ByKey("lot", descending: true)], Limit: 2), ct);
        byUsers.Groups.Select(g => $"{g.Key["lot"]}:{g.Measures[0].Value}").Should().Equal("C:3", "A:2");
        byUsers.Groups.Should().AllSatisfy(g => g.Measures[0].Value.Should().BeOfType<long>());

        var wrongKey = () => engine.AggregateAsync(type, new AggregateSpec(GroupBy: ["lot"], OrderBy: [GroupOrder.ByKey("qty")]), ct);
        var wrongMeasure = () => engine.AggregateAsync(type, new AggregateSpec(GroupBy: ["lot"], OrderBy: [GroupOrder.ByMeasure(0)]), ct);
        var noLimit = () => engine.AggregateAsync(type, new AggregateSpec(GroupBy: ["lot"], Limit: 0), ct);
        await wrongKey.Should().ThrowAsync<ArgumentException>().WithMessage("*does not group by*");
        await wrongMeasure.Should().ThrowAsync<ArgumentException>().WithMessage("*asks for 0*");
        await noLimit.Should().ThrowAsync<ArgumentOutOfRangeException>();

        await store.DropTableAsync(TableName, ct);
    }

    // Through the engine a sum or an average is a decimal and a bound is the column's own type, whatever
    // number type the store computed in; a measure a column cannot answer is refused, not answered empty.
    [Fact]
    public async Task An_aggregates_measures_are_typed_and_a_measure_the_column_cannot_answer_is_refused()
    {
        var store = CreateStore();
        var (engine, type) = Engine(store);
        var ct = TestContext.Current.CancellationToken;

        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":2}"""), cancellationToken: ct);
        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A","qty":5}"""), cancellationToken: ct);
        await engine.AcceptAsync(type, DocumentBody.Parse("""{"lot":"A"}"""), cancellationToken: ct);
        await engine.ProjectAsync(type, ct);

        var group = (await engine.AggregateAsync(type, new AggregateSpec(
            GroupBy: ["lot"],
            Measures: [AggregateMeasure.Sum("qty"), AggregateMeasure.Average("qty"), AggregateMeasure.Max("qty")]), ct)).Groups.Single();

        group.Count.Should().Be(3);
        group.Measures.Select(m => m.Value).Should().Equal(7m, 3.5m, 5L);
        group.Measures.Select(m => m.ValueCount).Should().AllBeEquivalentTo(2L, "the document without a qty took no part");

        var refused = () => engine.AggregateAsync(type, new AggregateSpec(Measures: [AggregateMeasure.Sum("lot"), AggregateMeasure.Min("lot"), AggregateMeasure.Sum("missing")]), ct);
        var thrown = (await refused.Should().ThrowAsync<InvalidQueryException>()).Which;
        thrown.InapplicableMeasures.Should().Equal(AggregateMeasure.Sum("lot"), AggregateMeasure.Min("lot"));
        thrown.UnknownColumns.Should().Equal("missing");

        await store.DropTableAsync(TableName, ct);
    }

    private TableSchema TypedSchema() =>
        new(TableName,
        [
            new ColumnDef("k", ColumnType.Text),
            new ColumnDef("amount", ColumnType.Decimal),
            new ColumnDef("at", ColumnType.Timestamp),
        ]);

    private static IReadOnlyDictionary<string, object?> TypedRow(string k, decimal amount, string at) =>
        new Dictionary<string, object?>
        {
            ["k"] = k,
            ["amount"] = amount,
            ["at"] = DateTimeOffset.Parse(at, CultureInfo.InvariantCulture),
        };

    private static string[] Keys(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) =>
        rows.Select(r => (string)r["k"]!).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task Decimal_ranges_compare_numerically_and_equality_ignores_trailing_zeros()
    {
        var store = CreateStore();
        await store.CreateTableAsync(TypedSchema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName,
        [
            TypedRow("a", 9.5m, "2026-01-01T00:00:00Z"),
            TypedRow("b", 10.25m, "2026-01-01T00:00:00Z"),
            TypedRow("c", 100m, "2026-01-01T00:00:00Z"),
        ], TestContext.Current.CancellationToken);

        var above = await store.QueryAsync(TableName, new QuerySpec(Filters: [new FieldFilter("amount", FilterOperator.GreaterThan, 10m)]), TestContext.Current.CancellationToken);
        Keys(above).Should().Equal("b", "c");

        var exact = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.Equal("amount", 10.250m)]), TestContext.Current.CancellationToken);
        Keys(exact).Should().Equal("b");

        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Timestamp_ranges_compare_instants_whatever_their_offset()
    {
        var store = CreateStore();
        await store.CreateTableAsync(TypedSchema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName,
        [
            TypedRow("early", 1m, "2026-09-01T08:00:00+09:00"),
            TypedRow("mid", 1m, "2026-09-14T00:00:00Z"),
            TypedRow("late", 1m, "2026-09-20T12:00:00-05:00"),
        ], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, new QuerySpec(Filters:
        [
            new FieldFilter("at", FilterOperator.GreaterThanOrEqual, DateTimeOffset.Parse("2026-09-14T09:00:00+09:00", CultureInfo.InvariantCulture)),
        ]), TestContext.Current.CancellationToken);

        Keys(rows).Should().Equal("late", "mid");
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    private static DateOnly? Day(object? value) => value switch
    {
        null => null,
        DateOnly day => day,
        DateTime instant => DateOnly.FromDateTime(instant),
        _ => DateOnly.Parse(Convert.ToString(value, CultureInfo.InvariantCulture)![..10], CultureInfo.InvariantCulture),
    };

    [Fact]
    public async Task Days_compare_as_days_in_equality_ranges_bounds_and_groups()
    {
        var store = CreateStore();
        var ct = TestContext.Current.CancellationToken;
        await store.CreateTableAsync(new TableSchema(TableName, [new ColumnDef("k", ColumnType.Text), new ColumnDef("on", ColumnType.Date)]), ct);
        await store.BulkInsertAsync(TableName,
        [
            new Dictionary<string, object?> { ["k"] = "a", ["on"] = new DateOnly(2026, 9, 30) },
            new Dictionary<string, object?> { ["k"] = "b", ["on"] = new DateOnly(2026, 10, 21) },
            new Dictionary<string, object?> { ["k"] = "c", ["on"] = new DateOnly(2026, 10, 21) },
            new Dictionary<string, object?> { ["k"] = "d", ["on"] = new DateOnly(2026, 11, 1) },
            new Dictionary<string, object?> { ["k"] = "e", ["on"] = null },
        ], ct);

        var same = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.Equal("on", new DateOnly(2026, 10, 21))]), ct);
        Keys(same).Should().Equal("b", "c");

        var october = await store.QueryAsync(TableName, new QuerySpec(Filters:
        [
            new FieldFilter("on", FilterOperator.GreaterThanOrEqual, new DateOnly(2026, 10, 1)),
            new FieldFilter("on", FilterOperator.LessThan, new DateOnly(2026, 11, 1)),
        ]), ct);
        Keys(october).Should().Equal("b", "c");

        var listed = await store.QueryAsync(TableName, new QuerySpec(Filters: [FieldFilter.In("on", [new DateOnly(2026, 9, 30), new DateOnly(2026, 11, 1)])]), ct);
        Keys(listed).Should().Equal("a", "d");

        Day(same[0]["on"]).Should().Be(new DateOnly(2026, 10, 21), "a day reads back as the same day");

        var bounds = await store.AggregateAsync(TableName, new AggregateSpec(
            Measures: [AggregateMeasure.Min("on"), AggregateMeasure.Max("on")]), ct);
        Day(bounds.Single().Measures[0].Value).Should().Be(new DateOnly(2026, 9, 30));
        Day(bounds.Single().Measures[1].Value).Should().Be(new DateOnly(2026, 11, 1));

        var byDay = await store.AggregateAsync(TableName, new AggregateSpec(GroupBy: ["on"]), ct);
        byDay.ToDictionary(g => Day(g.Key["on"])?.ToString("O", CultureInfo.InvariantCulture) ?? "none", g => g.Count)
            .Should().BeEquivalentTo(new Dictionary<string, long> { ["2026-09-30"] = 1, ["2026-10-21"] = 2, ["2026-11-01"] = 1, ["none"] = 1 });

        await store.DropTableAsync(TableName, ct);
    }

    [Fact]
    public async Task Text_matching_ignores_case_beyond_ascii()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema(), TestContext.Current.CancellationToken);
        await store.BulkInsertAsync(TableName, [Row("Ärger im Büro", 1), Row("ärger", 2), Row("Ordnung", 3)], TestContext.Current.CancellationToken);

        var rows = await store.QueryAsync(TableName, new QuerySpec(Filters: [new FieldFilter("k", FilterOperator.StartsWith, "äRG")]), TestContext.Current.CancellationToken);

        Values(rows).Should().Equal(1L, 2L);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    private TableSchema ProjectedSchema() =>
        new(TableName, [.. ProjectionSystemColumns.All, new ColumnDef("v", ColumnType.Integer)]);

    private static IReadOnlyDictionary<string, object?> ProjectedRow(DocumentId document, string? key, long v) =>
        new Dictionary<string, object?>
        {
            [ProjectionSystemColumns.DocumentId] = document.Value,
            [ProjectionSystemColumns.Watermark] = v,
            [ProjectionSystemColumns.RecordKey] = key,
            [ProjectionSystemColumns.Record] = ProjectionSystemColumns.RecordOf(document, key is null ? null : RecordKey.Create(key)),
            ["v"] = v,
        };

    [Fact]
    public async Task Replacing_rows_removes_by_record_key_and_by_document_then_inserts()
    {
        var store = CreateStore();
        await store.CreateTableAsync(ProjectedSchema(), TestContext.Current.CancellationToken);
        var (a, b, c, d) = (DocumentId.New(), DocumentId.New(), DocumentId.New(), DocumentId.New());
        await store.BulkInsertAsync(TableName, [ProjectedRow(a, "k1", 1), ProjectedRow(b, "k2", 2), ProjectedRow(c, null, 3)], TestContext.Current.CancellationToken);

        var inserted = await store.ReplaceRowsAsync(TableName, [RecordKey.Create("k1"), RecordKey.Create("absent")], [c, DocumentId.New()], [ProjectedRow(d, "k1", 4)], TestContext.Current.CancellationToken);

        inserted.Should().Be(1);
        Values(await store.QueryAsync(TableName, QuerySpec.All, TestContext.Current.CancellationToken)).Order().Should().Equal(2L, 4L);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replacing_the_same_rows_twice_leaves_the_table_as_once()
    {
        var store = CreateStore();
        await store.CreateTableAsync(ProjectedSchema(), TestContext.Current.CancellationToken);
        var (old, corrected, standalone) = (DocumentId.New(), DocumentId.New(), DocumentId.New());
        await store.BulkInsertAsync(TableName, [ProjectedRow(old, "k1", 1)], TestContext.Current.CancellationToken);
        IReadOnlyList<IReadOnlyDictionary<string, object?>> delta = [ProjectedRow(corrected, "k1", 2), ProjectedRow(standalone, null, 3)];

        // A retry after a failure between the rows and the projection state applies the same delta again.
        await store.ReplaceRowsAsync(TableName, [RecordKey.Create("k1")], [corrected, standalone], delta, TestContext.Current.CancellationToken);
        await store.ReplaceRowsAsync(TableName, [RecordKey.Create("k1")], [corrected, standalone], delta, TestContext.Current.CancellationToken);

        Values(await store.QueryAsync(TableName, QuerySpec.All, TestContext.Current.CancellationToken)).Order().Should().Equal(2L, 3L);
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    /// <summary>A record retired when it was the only one: its row goes, nothing comes in, and the table is empty.</summary>
    [Fact]
    public async Task Replacing_the_only_row_with_none_leaves_the_table_empty()
    {
        var store = CreateStore();
        await store.CreateTableAsync(ProjectedSchema(), TestContext.Current.CancellationToken);
        var (only, retired) = (DocumentId.New(), DocumentId.New());
        await store.BulkInsertAsync(TableName, [ProjectedRow(only, "k1", 1)], TestContext.Current.CancellationToken);

        var inserted = await store.ReplaceRowsAsync(TableName, [RecordKey.Create("k1")], [retired], [], TestContext.Current.CancellationToken);

        inserted.Should().Be(0);
        (await store.QueryAsync(TableName, QuerySpec.All, TestContext.Current.CancellationToken)).Should().BeEmpty();
        await store.DropTableAsync(TableName, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Replacing_rows_in_a_missing_table_fails()
    {
        var store = CreateStore();

        var act = () => store.ReplaceRowsAsync(TableName, [], [], [ProjectedRow(DocumentId.New(), null, 1)], TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<Exception>();
    }
}
