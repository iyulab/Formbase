using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

public class ProjectorTests
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

        public Harness()
        {
            Intake = new IntakeService(Raw);
            Projector = new Projector(Raw, new HintSchemaProposer(Hints), Store, State);
        }

        public void DeclareQcHints() => Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));

        public Task Accept(string json) => Intake.AcceptAsync(Qc, DocumentBody.Parse(json));
    }

    [Fact]
    public async Task Projecting_without_hints_is_a_no_op()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1","qty":1}""");

        var result = await h.Projector.ProjectAsync(Qc);

        result.Projected.Should().BeFalse();
        (await h.State.GetProjectedWatermarkAsync(Qc)).Should().BeNull();
        (await h.Store.TableExistsAsync(Table)).Should().BeFalse();
    }

    [Fact]
    public async Task Projecting_with_hints_builds_the_table_and_records_the_watermark()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Accept("""{"lot":"L-2","qty":20}""");

        var result = await h.Projector.ProjectAsync(Qc);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(2);
        result.Skipped.Should().BeEmpty();
        (await h.State.GetProjectedWatermarkAsync(Qc)).Should().Be(new Watermark(2));

        var rows = await h.Store.QueryAsync(Table, QuerySpec.All);
        rows.Should().HaveCount(2);
        rows[0].Should().ContainKeys(ProjectionSystemColumns.DocumentId, ProjectionSystemColumns.Watermark, "lot", "qty");
        rows[0]["lot"].Should().Be("L-1");
        rows[0]["qty"].Should().Be(10L);
    }

    [Fact]
    public async Task A_missing_required_field_skips_only_that_document()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Accept("""{"qty":20}""");           // 'lot' is required and absent
        await h.Accept("""{"lot":"L-3"}""");        // 'qty' is nullable and absent — OK

        var result = await h.Projector.ProjectAsync(Qc);

        result.Inserted.Should().Be(2);
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("lot");

        var rows = await h.Store.QueryAsync(Table, QuerySpec.All);
        rows.Should().HaveCount(2);
        rows.Should().Contain(r => Equals(r["lot"], "L-3") && r["qty"] == null);
    }

    [Fact]
    public async Task A_type_mismatch_skips_the_document()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":"not-a-number"}""");

        var result = await h.Projector.ProjectAsync(Qc);

        result.Inserted.Should().Be(0);
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("qty");
    }

    [Fact]
    public async Task Re_projection_rebuilds_from_raw_and_advances_the_watermark()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Projector.ProjectAsync(Qc);

        await h.Accept("""{"lot":"L-2","qty":2}""");
        var second = await h.Projector.ProjectAsync(Qc);

        second.Inserted.Should().Be(2, "drop-and-rebuild reprojects the whole raw stream, not just the delta");
        (await h.State.GetProjectedWatermarkAsync(Qc)).Should().Be(new Watermark(2));
        (await h.Store.QueryAsync(Table, QuerySpec.All)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Re_projecting_unchanged_raw_does_not_duplicate_rows()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Projector.ProjectAsync(Qc);

        await h.Projector.ProjectAsync(Qc);

        (await h.Store.QueryAsync(Table, QuerySpec.All)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failed_rebuild_leaves_the_state_not_projected()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, Table, [new FieldHint("lot", ColumnType.Text)]));
        var state = new InMemoryProjectionState();
        var projector = new Projector(raw, new HintSchemaProposer(hints), new ThrowingProjectionStore(), state);
        await new IntakeService(raw).AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""));

        var act = () => projector.ProjectAsync(Qc);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await state.GetProjectedWatermarkAsync(Qc)).Should().BeNull("a failed rebuild must not leave a projected watermark");
    }

    [Fact]
    public async Task A_document_arriving_mid_run_is_left_for_the_next_projection()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Accept("""{"lot":"L-2","qty":2}""");
        var head = await h.Raw.HeadAsync(Qc);
        await h.Accept("""{"lot":"L-3","qty":3}"""); // lands after the run's head was captured

        // Intake never blocks on projection, so documents keep landing while a run streams raw. A store
        // whose stream is not snapshot-isolated will hand those later documents to the run — the port
        // promises no isolation, so the projector must bound the run itself.
        var live = new StreamsPastCapturedHead(h.Raw, head);
        var result = await new Projector(live, new HintSchemaProposer(h.Hints), h.Store, h.State).ProjectAsync(Qc);

        result.Inserted.Should().Be(2, "only the documents at or below the captured head belong to this run");
        var lots = (await h.Store.QueryAsync(Table, QuerySpec.All)).Select(row => row["lot"]);
        lots.Should().BeEquivalentTo(["L-1", "L-2"]);
        (await h.State.GetProjectedWatermarkAsync(Qc)).Should().Be(head,
            "the recorded watermark must describe the rows actually written, not raw's later head");
    }

    [Fact]
    public async Task The_next_projection_picks_up_what_the_previous_run_left()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        var head = await h.Raw.HeadAsync(Qc);
        await h.Accept("""{"lot":"L-2","qty":2}""");
        var live = new StreamsPastCapturedHead(h.Raw, head);
        await new Projector(live, new HintSchemaProposer(h.Hints), h.Store, h.State).ProjectAsync(Qc);

        // Bounding a run defers the straggler, it does not drop it.
        var second = await h.Projector.ProjectAsync(Qc);

        second.Inserted.Should().Be(2);
        (await h.State.GetProjectedWatermarkAsync(Qc)).Should().Be(new Watermark(2));
    }

    /// <summary>
    /// A raw store that reports a fixed head but streams everything it currently holds — standing in
    /// for a backend whose stream is not snapshot-isolated, where documents that arrive mid-run show up
    /// in the same enumeration. The in-memory store copies its log before yielding, so it can never
    /// exercise the projector's bound on its own.
    /// </summary>
    private sealed class StreamsPastCapturedHead(IRawStore inner, Watermark capturedHead) : IRawStore
    {
        public IAsyncEnumerable<StoredDocument> StreamAsync(FormTypeRef type, Watermark after, CancellationToken cancellationToken = default)
            => inner.StreamAsync(type, after, cancellationToken);

        public Task<Watermark> HeadAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromResult(capturedHead);

        public Task<StoredDocument> AppendAsync(FormTypeRef type, DocumentId id, DocumentBody body, CancellationToken cancellationToken = default)
            => inner.AppendAsync(type, id, body, cancellationToken);
        public Task<StoredDocument?> GetAsync(DocumentId id, CancellationToken cancellationToken = default)
            => inner.GetAsync(id, cancellationToken);
    }

    private sealed class ThrowingProjectionStore : IProjectionStore
    {
        public Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task DropTableAsync(string tableName, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("bulk insert failed");
        public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>([]);
    }
}
