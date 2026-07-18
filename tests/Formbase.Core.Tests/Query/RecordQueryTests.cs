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
