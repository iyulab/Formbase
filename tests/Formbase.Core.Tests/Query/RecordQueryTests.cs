using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Query;

public class RecordQueryTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private const string Table = "qc";

    private sealed class Harness
    {
        public InMemoryRawStore Raw { get; } = new();
        public InMemoryFieldHintSource Hints { get; } = new();
        public InMemoryProjectionStore Store { get; } = new();
        public InMemoryProjectionState State { get; } = new();
        public IntakeService Intake { get; }
        public Projector Projector { get; }
        public RecordQuery Query { get; }

        public Harness(IProjectionStore? queryStore = null)
        {
            Intake = new IntakeService(Raw);
            var proposer = new HintSchemaProposer(Hints);
            Projector = new Projector(Raw, proposer, Store, State);
            Query = new RecordQuery(Raw, proposer, queryStore ?? Store, State);
        }

        public void DeclareHints() => Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));

        public void DeclareHintsWithInstant() => Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
            new FieldHint("at", ColumnType.Timestamp, Nullable: true),
        ]));

        public Task<DocumentId> Accept(string json) => Intake.AcceptAsync(Qc, DocumentBody.Parse(json));
    }

    [Fact]
    public async Task Querying_an_unprojected_form_type_throws_NotProjected()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        // Documents accepted but never projected.

        var act = () => h.Query.QueryAsync(Qc, QuerySpec.All);

        await act.Should().ThrowAsync<NotProjectedException>();
    }

    [Fact]
    public async Task Querying_a_projected_form_type_returns_rows_and_is_not_stale()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Accept("""{"lot":"L-2","qty":20}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All, TestContext.Current.CancellationToken);

        result.Stale.Should().BeFalse();
        result.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_projected_but_empty_match_is_a_result_not_NotProjected()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var result = await h.Query.QueryAsync(Qc, new QuerySpec(
            Filters: [FieldFilter.Equal("lot", "does-not-exist")]), TestContext.Current.CancellationToken);

        result.Rows.Should().BeEmpty();
        result.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task Raw_advancing_past_the_projection_makes_the_query_stale()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        await h.Accept("""{"lot":"L-2","qty":20}"""); // appended after projection

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All, TestContext.Current.CancellationToken);

        result.Stale.Should().BeTrue();
        result.Rows.Should().HaveCount(1, "the query still serves the last projected snapshot");
    }

    [Fact]
    public async Task Query_rows_carry_exactly_the_declared_fields()
    {
        // The row contract is the declaration, nothing else: fb_* bookkeeping and any backend system
        // columns are hidden layers. A consumer serializing rows onward must not be handed internals
        // that then calcify into *its* public contract.
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All, TestContext.Current.CancellationToken);

        result.Rows[0].Fields.Keys.Should().BeEquivalentTo(["lot", "qty"]);
    }

    [Fact]
    public async Task Redeclaring_columns_without_reprojecting_makes_the_query_stale()
    {
        // C1 case A: hints were redeclared (a column added) but ProjectAsync never re-ran. No new
        // documents arrived, so the watermark alone would report "fresh" — a silent wrong answer.
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        h.Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
            new FieldHint("inspector", ColumnType.Text),
        ]));

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All, TestContext.Current.CancellationToken);

        result.Stale.Should().BeTrue("the projected table no longer matches the declared shape");
        result.Rows.Should().HaveCount(1, "the last projected snapshot still serves");
        // The row contract holds even under drift: the declared key set, with the not-yet-projected
        // column reading null rather than being absent.
        result.Rows[0].Fields.Keys.Should().BeEquivalentTo(["lot", "qty", "inspector"]);
        result.Rows[0].Fields["inspector"].Should().BeNull();
    }

    [Fact]
    public async Task Redeclaring_the_table_name_without_reprojecting_throws_NotProjected()
    {
        // C1 case B: the declaration moved to a table that was never built. Reporting the missing
        // table as ProjectionUnavailable would misdiagnose a re-projection gap as a backend outage.
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        h.Hints.Declare(new FormTypeHints(Qc, "qc_v2",
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));

        var act = () => h.Query.QueryAsync(Qc, QuerySpec.All);

        await act.Should().ThrowAsync<NotProjectedException>();
    }

    [Fact]
    public async Task Reprojecting_after_a_redeclaration_restores_freshness()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        h.Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
            new FieldHint("inspector", ColumnType.Text),
        ]));
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All, TestContext.Current.CancellationToken);

        result.Stale.Should().BeFalse("re-projection materialized the redeclared shape");
    }

    [Fact]
    public async Task An_int_filter_matches_a_long_stored_value_via_coercion()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Accept("""{"lot":"L-2","qty":20}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        // Filter value is a C# int; the stored value is a long. Coercion must bridge them.
        var result = await h.Query.QueryAsync(Qc, new QuerySpec(
            Filters: [FieldFilter.Equal("qty", 20)]), TestContext.Current.CancellationToken);

        result.Rows.Should().ContainSingle();
        result.Rows[0].Fields["lot"].Should().Be("L-2");
    }

    [Fact]
    public async Task A_backing_store_failure_surfaces_as_ProjectionUnavailable()
    {
        var h = new Harness(queryStore: new ThrowingQueryStore());
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken); // projects via the real store; State records it

        var act = () => h.Query.QueryAsync(Qc, QuerySpec.All);

        await act.Should().ThrowAsync<ProjectionUnavailableException>();
    }

    [Fact]
    public async Task An_unordered_query_still_reaches_the_store_ordered_by_watermark()
    {
        var spy = new SpecCapturingQueryStore();
        var h = new Harness(queryStore: spy);
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        await h.Query.QueryAsync(Qc, QuerySpec.All, TestContext.Current.CancellationToken);

        // Paging is only well-defined over a total order. Callers rarely supply one, so the read path
        // appends the unique, monotonic watermark as the last key — asserted on the spec handed to the
        // store, because whether a *result* comes back stable depends on the backing store's sort
        // (an in-memory LINQ sort is stable and would hide a missing tie-break entirely).
        spy.Captured!.OrderBy.Should().ContainSingle()
            .Which.Column.Should().Be(ProjectionSystemColumns.Watermark);
    }

    [Fact]
    public async Task A_caller_supplied_order_keeps_its_keys_and_gains_the_watermark_tie_break()
    {
        var spy = new SpecCapturingQueryStore();
        var h = new Harness(queryStore: spy);
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        await h.Query.QueryAsync(Qc, new QuerySpec(OrderBy: [new OrderKey("lot", Descending: true)]), TestContext.Current.CancellationToken);

        // The caller's intent leads; the tie-break only breaks ties beneath it. Ordering the watermark
        // first would silently override what the caller asked for.
        spy.Captured!.OrderBy.Should().HaveCount(2);
        spy.Captured.OrderBy![0].Should().Be(new OrderKey("lot", Descending: true));
        spy.Captured.OrderBy[1].Column.Should().Be(ProjectionSystemColumns.Watermark);
    }

    /// <summary>Records the spec the read path actually hands to the projection store.</summary>
    [Theory]
    [InlineData("qty", FilterOperator.Contains, "1")]
    [InlineData("lot", FilterOperator.GreaterThan, "L-1")]
    [InlineData("qty", FilterOperator.GreaterThan, null)]
    [InlineData("lot", FilterOperator.StartsWith, null)]
    [InlineData("qty", FilterOperator.IsNull, "10")]
    [InlineData("lot", FilterOperator.IsNotNull, "L-1")]
    public async Task A_filter_whose_operator_the_column_cannot_answer_is_refused(string column, FilterOperator op, string? value)
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var act = () => h.Query.QueryAsync(Qc, new QuerySpec(Filters: [new FieldFilter(column, op, value)]));

        (await act.Should().ThrowAsync<InvalidQueryException>())
            .Which.InapplicableFilters.Should().ContainSingle().Which.Column.Should().Be(column);
    }

    [Fact]
    public async Task A_range_filter_value_is_coerced_to_the_column_type()
    {
        var h = new Harness();
        h.DeclareHintsWithInstant();
        await h.Accept("""{"lot":"L-1","qty":10,"at":"2026-09-01T00:00:00Z"}""");
        await h.Accept("""{"lot":"L-2","qty":20,"at":"2026-09-20T00:00:00Z"}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var result = await h.Query.QueryAsync(Qc, new QuerySpec(Filters:
        [
            new FieldFilter("at", FilterOperator.GreaterThanOrEqual, "2026-09-14T00:00:00Z"),
            new FieldFilter("qty", FilterOperator.LessThan, 100),
        ]), TestContext.Current.CancellationToken);

        result.Rows.Should().ContainSingle().Which.Fields["lot"].Should().Be("L-2");
    }

    [Fact]
    public async Task A_date_only_filter_value_matches_the_same_date_only_document_value()
    {
        var h = new Harness();
        h.DeclareHintsWithInstant();
        await h.Accept("""{"lot":"L-1","at":"2026-01-14"}""");
        await h.Accept("""{"lot":"L-2","at":"2026-01-15"}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var equal = await h.Query.QueryAsync(Qc, new QuerySpec(Filters:
            [new FieldFilter("at", FilterOperator.Equal, "2026-01-15")]), TestContext.Current.CancellationToken);
        var fromUtcMidnight = await h.Query.QueryAsync(Qc, new QuerySpec(Filters:
            [new FieldFilter("at", FilterOperator.GreaterThanOrEqual, "2026-01-15T00:00:00Z")]), TestContext.Current.CancellationToken);

        equal.Rows.Should().ContainSingle().Which.Fields["lot"].Should().Be("L-2");
        fromUtcMidnight.Rows.Should().ContainSingle().Which.Fields["lot"].Should().Be("L-2",
            "a date written without an offset is that date's UTC midnight, on the document side and the filter side alike");
    }

    [Fact]
    public async Task Aggregating_an_unprojected_form_type_throws_NotProjected()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");

        var act = () => h.Query.AggregateAsync(Qc, AggregateSpec.CountAll);

        await act.Should().ThrowAsync<NotProjectedException>();
    }

    [Fact]
    public async Task Aggregate_groups_come_back_in_key_order_nulls_first_and_flag_staleness()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"B","qty":1}""");
        await h.Accept("""{"lot":"A","qty":2}""");
        await h.Accept("""{"lot":"B"}""");
        await h.Accept("""{"lot":"A","qty":2}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        await h.Accept("""{"lot":"C","qty":3}""");

        var result = await h.Query.AggregateAsync(Qc, new AggregateSpec(GroupBy: ["lot", "qty"]), TestContext.Current.CancellationToken);

        result.Stale.Should().BeTrue("a document arrived after the projection");
        result.Groups.Select(g => (g.Key["lot"], g.Key["qty"], g.Count)).Should().Equal(
            ("A", (object?)2L, 2L),
            ("B", null, 1L),
            ("B", 1L, 1L));
    }

    // The records a group's count is made of come back with it, and the group's own query reads every
    // record behind it — the same rows, including past the limit, a null key included.
    [Fact]
    public async Task A_groups_records_come_with_its_count_and_its_query_reads_the_same_records()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"B","qty":1}""");
        var a2 = await h.Accept("""{"lot":"A","qty":2}""");
        await h.Accept("""{"lot":"C"}""");
        var a3 = await h.Accept("""{"lot":"A","qty":3}""");
        await h.Accept("""{"lot":"A","qty":4}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);
        var spec = new AggregateSpec(GroupBy: ["lot"], Filters: [new FieldFilter("qty", FilterOperator.GreaterThanOrEqual, 2L)], RecordsPerGroup: 2);

        var result = await h.Query.AggregateAsync(Qc, spec, TestContext.Current.CancellationToken);

        var a = result.Groups.Single(g => Equals(g.Key["lot"], "A"));
        a.Count.Should().Be(3);
        a.Records.Should().Equal([new RecordRef(a2, null), new RecordRef(a3, null)],
            "the first of its records in the order they were accepted — the limit cuts the list, not the count");
        var all = await h.Query.QueryAsync(Qc, spec.RecordsOf(a), TestContext.Current.CancellationToken);
        all.Rows.Select(r => r.Fields["qty"]).Should().Equal(2L, 3L, 4L);
        all.Rows.Take(2).Select(r => r.Record).Should().Equal(a.Records, "the evidence names the same records the group's rows are");

        var byQty = new AggregateSpec(GroupBy: ["qty"], RecordsPerGroup: 1);
        var noQty = (await h.Query.AggregateAsync(Qc, byQty, TestContext.Current.CancellationToken)).Groups.Single(g => g.Key["qty"] is null);
        (await h.Query.QueryAsync(Qc, byQty.RecordsOf(noQty), TestContext.Current.CancellationToken)).Rows
            .Select(r => r.Fields["lot"]).Should().Equal("C");
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Asking_for_no_records_per_group_is_refused(int records)
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var act = () => h.Query.AggregateAsync(Qc, new AggregateSpec(RecordsPerGroup: records));

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Aggregate_keys_come_back_as_the_declared_column_type()
    {
        var store = new StubAggregateStore([new AggregateGroup(new Dictionary<string, object?> { ["at"] = "2026-09-01T00:00:00Z" }, 3)]);
        var h = new Harness(store);
        h.DeclareHintsWithInstant();
        await h.Accept("""{"lot":"L-1","qty":1,"at":"2026-09-01T00:00:00Z"}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var result = await h.Query.AggregateAsync(Qc, new AggregateSpec(GroupBy: ["at"]), TestContext.Current.CancellationToken);

        result.Groups.Should().ContainSingle().Which.Key["at"].Should().Be(DateTimeOffset.Parse("2026-09-01T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
            "a wire that carries instants as text must not make a group key unequal to the same column's filter value");
    }

    [Fact]
    public async Task Grouping_by_an_undeclared_column_is_refused()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var act = () => h.Query.AggregateAsync(Qc, new AggregateSpec(GroupBy: ["fb_watermark"]));

        (await act.Should().ThrowAsync<InvalidQueryException>()).Which.UnknownColumns.Should().Equal("fb_watermark");
    }

    [Fact]
    public async Task An_aggregate_backing_store_failure_surfaces_as_ProjectionUnavailable()
    {
        var h = new Harness(new ThrowingQueryStore());
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var act = () => h.Query.AggregateAsync(Qc, AggregateSpec.CountAll);

        await act.Should().ThrowAsync<ProjectionUnavailableException>();
    }

    private sealed class StubAggregateStore(IReadOnlyList<AggregateGroup> groups) : IProjectionStore
    {
        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
            => Task.FromResult(rows.Count);

        public Task<int> ReplaceRowsAsync(string tableName, IReadOnlyCollection<RecordKey> removeKeys, IReadOnlyCollection<DocumentId> removeDocuments, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);

        public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult(groups);
    }

    private sealed class SpecCapturingQueryStore : IProjectionStore
    {
        public QuerySpec? Captured { get; private set; }

        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count);

        public Task<int> ReplaceRowsAsync(string tableName, IReadOnlyCollection<RecordKey> removeKeys, IReadOnlyCollection<DocumentId> removeDocuments, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
        {
            Captured = spec;
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        }

        public AggregateSpec? CapturedAggregate { get; private set; }

        public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default)
        {
            CapturedAggregate = spec;
            return Task.FromResult<IReadOnlyList<AggregateGroup>>([]);
        }
    }

    private sealed class ThrowingQueryStore : IProjectionStore
    {
        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count);
        public Task<int> ReplaceRowsAsync(string tableName, IReadOnlyCollection<RecordKey> removeKeys, IReadOnlyCollection<DocumentId> removeDocuments, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count);
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
            => throw new TimeoutException("backing store unreachable");

        public Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default)
            => throw new TimeoutException("backing store unreachable");
    }
}
