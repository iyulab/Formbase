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

        public Task Accept(string json) => Intake.AcceptAsync(Qc, DocumentBody.Parse(json));
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
        await h.Projector.ProjectAsync(Qc);

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All);

        result.Stale.Should().BeFalse();
        result.Rows.Should().HaveCount(2);
    }

    [Fact]
    public async Task A_projected_but_empty_match_is_a_result_not_NotProjected()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc);

        var result = await h.Query.QueryAsync(Qc, new QuerySpec(
            Filters: new Dictionary<string, object?> { ["lot"] = "does-not-exist" }));

        result.Rows.Should().BeEmpty();
        result.Stale.Should().BeFalse();
    }

    [Fact]
    public async Task Raw_advancing_past_the_projection_makes_the_query_stale()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc);
        await h.Accept("""{"lot":"L-2","qty":20}"""); // appended after projection

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All);

        result.Stale.Should().BeTrue();
        result.Rows.Should().HaveCount(1, "the query still serves the last projected snapshot");
    }

    [Fact]
    public async Task Redeclaring_columns_without_reprojecting_makes_the_query_stale()
    {
        // C1 case A: hints were redeclared (a column added) but ProjectAsync never re-ran. No new
        // documents arrived, so the watermark alone would report "fresh" — a silent wrong answer.
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc);

        h.Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
            new FieldHint("inspector", ColumnType.Text),
        ]));

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All);

        result.Stale.Should().BeTrue("the projected table no longer matches the declared shape");
        result.Rows.Should().HaveCount(1, "the last projected snapshot still serves");
    }

    [Fact]
    public async Task Redeclaring_the_table_name_without_reprojecting_throws_NotProjected()
    {
        // C1 case B: the declaration moved to a table that was never built. Reporting the missing
        // table as ProjectionUnavailable would misdiagnose a re-projection gap as a backend outage.
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc);

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
        await h.Projector.ProjectAsync(Qc);

        h.Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
            new FieldHint("inspector", ColumnType.Text),
        ]));
        await h.Projector.ProjectAsync(Qc);

        var result = await h.Query.QueryAsync(Qc, QuerySpec.All);

        result.Stale.Should().BeFalse("re-projection materialized the redeclared shape");
    }

    [Fact]
    public async Task An_int_filter_matches_a_long_stored_value_via_coercion()
    {
        var h = new Harness();
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Accept("""{"lot":"L-2","qty":20}""");
        await h.Projector.ProjectAsync(Qc);

        // Filter value is a C# int; the stored value is a long. Coercion must bridge them.
        var result = await h.Query.QueryAsync(Qc, new QuerySpec(
            Filters: new Dictionary<string, object?> { ["qty"] = 20 }));

        result.Rows.Should().ContainSingle();
        result.Rows[0]["lot"].Should().Be("L-2");
    }

    [Fact]
    public async Task A_backing_store_failure_surfaces_as_ProjectionUnavailable()
    {
        var h = new Harness(queryStore: new ThrowingQueryStore());
        h.DeclareHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Projector.ProjectAsync(Qc); // projects via the real store; State records it

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
        await h.Projector.ProjectAsync(Qc);

        await h.Query.QueryAsync(Qc, QuerySpec.All);

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
        await h.Projector.ProjectAsync(Qc);

        await h.Query.QueryAsync(Qc, new QuerySpec(OrderBy: [new OrderKey("lot", Descending: true)]));

        // The caller's intent leads; the tie-break only breaks ties beneath it. Ordering the watermark
        // first would silently override what the caller asked for.
        spy.Captured!.OrderBy.Should().HaveCount(2);
        spy.Captured.OrderBy![0].Should().Be(new OrderKey("lot", Descending: true));
        spy.Captured.OrderBy[1].Column.Should().Be(ProjectionSystemColumns.Watermark);
    }

    /// <summary>Records the spec the read path actually hands to the projection store.</summary>
    private sealed class SpecCapturingQueryStore : IProjectionStore
    {
        public QuerySpec? Captured { get; private set; }

        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count);

        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
        {
            Captured = spec;
            return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
        }
    }

    private sealed class ThrowingQueryStore : IProjectionStore
    {
        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => Task.FromResult(true);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default) => Task.FromResult(rows.Count);
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
            => throw new TimeoutException("backing store unreachable");
    }
}
