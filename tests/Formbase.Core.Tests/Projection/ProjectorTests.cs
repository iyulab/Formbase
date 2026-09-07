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

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Projected.Should().BeFalse();
        (await h.State.GetAsync(Qc, TestContext.Current.CancellationToken)).Should().BeNull();
        (await h.Store.TableExistsAsync(Table, TestContext.Current.CancellationToken)).Should().BeFalse();
    }

    [Fact]
    public async Task Projecting_with_hints_builds_the_table_and_records_the_watermark()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":10}""");
        await h.Accept("""{"lot":"L-2","qty":20}""");

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Projected.Should().BeTrue();
        result.Inserted.Should().Be(2);
        result.Skipped.Should().BeEmpty();
        (await h.State.GetAsync(Qc, TestContext.Current.CancellationToken))?.Watermark.Should().Be(new Watermark(2));

        var rows = await h.Store.QueryAsync(Table, QuerySpec.All, TestContext.Current.CancellationToken);
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

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(2);
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("lot");

        var rows = await h.Store.QueryAsync(Table, QuerySpec.All, TestContext.Current.CancellationToken);
        rows.Should().HaveCount(2);
        rows.Should().Contain(r => Equals(r["lot"], "L-3") && r["qty"] == null);
    }

    [Fact]
    public async Task Absent_fields_are_counted_apart_from_explicit_nulls()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":null}"""); // explicit null — the writer said "no value"
        await h.Accept("""{"lot":"L-2"}""");            // absent — the field did not exist for this document
        await h.Accept("""{"lot":"L-3","qty":3}""");

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(3);
        result.AbsentFieldCounts.Should().Equal(new Dictionary<string, int> { ["qty"] = 1 },
            "an explicit null is an answer; a field the document never had is a different fact");
    }

    [Fact]
    public async Task Absence_counts_cover_only_rows_that_landed()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"qty":1}"""); // skipped — required 'lot' absent; its absences are reported via the skip, not the counts

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Skipped.Should().ContainSingle();
        result.AbsentFieldCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_required_field_skip_names_absent_and_null_differently()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"qty":1}""");              // 'lot' absent
        await h.Accept("""{"lot":null,"qty":2}""");   // 'lot' explicitly null

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Skipped.Should().HaveCount(2);
        result.Skipped[0].Reason.Should().Contain("absent");
        result.Skipped[1].Reason.Should().Contain("null");
    }

    [Fact]
    public async Task A_no_schema_result_reports_no_absences()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1"}""");

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Projected.Should().BeFalse();
        result.AbsentFieldCounts.Should().BeEmpty();
    }

    [Fact]
    public async Task A_structured_value_in_a_text_column_is_a_skip_not_a_json_string()
    {
        // Text was the one target that filled a structural mismatch with a plausible value: an array
        // arriving for a text column landed as its JSON ('["L-1"]'), skip-free, where Integer or
        // Timestamp would have recorded a skip. Same input, same answer now -- and the reason points
        // at the declaration that keeps the structure.
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":["L-1","L-2"],"qty":1}""");
        await h.Accept("""{"lot":{"id":"L-3"},"qty":2}""");
        await h.Accept("""{"lot":"L-4","qty":3}""");

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(1);
        result.Skipped.Should().HaveCount(2);
        result.Skipped.Should().AllSatisfy(s => s.Reason.Should().Contain("lot").And.Contain("Jsonb"));
        var rows = await h.Store.QueryAsync(Table, QuerySpec.All, TestContext.Current.CancellationToken);
        rows.Should().ContainSingle().Which["lot"].Should().Be("L-4");
    }

    [Fact]
    public async Task A_scalar_that_is_not_a_string_still_projects_into_a_text_column()
    {
        // The structural rule is about arrays and objects only. A number or a boolean has one text
        // form and keeps landing as it did.
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":42,"qty":1}""");
        await h.Accept("""{"lot":true,"qty":2}""");

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(2);
        result.Skipped.Should().BeEmpty();
    }

    [Fact]
    public async Task A_type_mismatch_skips_the_document()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":"not-a-number"}""");

        var result = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(0);
        result.Skipped.Should().ContainSingle().Which.Reason.Should().Contain("qty");
    }

    [Fact]
    public async Task Re_projection_rebuilds_from_raw_and_advances_the_watermark()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        await h.Accept("""{"lot":"L-2","qty":2}""");
        var second = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        second.Inserted.Should().Be(2, "drop-and-rebuild reprojects the whole raw stream, not just the delta");
        (await h.State.GetAsync(Qc, TestContext.Current.CancellationToken))?.Watermark.Should().Be(new Watermark(2));
        (await h.Store.QueryAsync(Table, QuerySpec.All, TestContext.Current.CancellationToken)).Should().HaveCount(2);
    }

    [Fact]
    public async Task Re_projecting_unchanged_raw_does_not_duplicate_rows()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        (await h.Store.QueryAsync(Table, QuerySpec.All, TestContext.Current.CancellationToken)).Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failed_rebuild_leaves_the_state_not_projected()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, Table, [new FieldHint("lot", ColumnType.Text)]));
        var state = new InMemoryProjectionState();
        var projector = new Projector(raw, new HintSchemaProposer(hints), new ThrowingProjectionStore(), state);
        await new IntakeService(raw).AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), cancellationToken: TestContext.Current.CancellationToken);

        var act = () => projector.ProjectAsync(Qc);

        await act.Should().ThrowAsync<InvalidOperationException>();
        (await state.GetAsync(Qc, TestContext.Current.CancellationToken)).Should().BeNull("a failed rebuild must not leave a projected stamp");
    }

    [Fact]
    public async Task A_clear_failure_does_not_replace_the_rebuild_failure()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, Table, [new FieldHint("lot", ColumnType.Text)]));
        var state = new ThrowingProjectionState(new TimeoutException("state store unreachable"));
        var projector = new Projector(raw, new HintSchemaProposer(hints), new ThrowingProjectionStore(), state);
        await new IntakeService(raw).AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), cancellationToken: TestContext.Current.CancellationToken);

        var act = () => projector.ProjectAsync(Qc);

        // The durable state shares the store's connection pool, so the outage that failed the rebuild
        // is likely to fail the cleanup too. The caller must still see the rebuild failure — a cleanup
        // exception replacing it would hide the original cause entirely.
        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("bulk insert failed");
        thrown.Data[Projector.ClearFailureDataKey].Should().BeOfType<TimeoutException>(
            "the cleanup failure must travel with the original cause, not vanish");
    }

    [Fact]
    public async Task A_failed_rebuild_with_working_cleanup_carries_no_clear_failure()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, Table, [new FieldHint("lot", ColumnType.Text)]));
        var projector = new Projector(raw, new HintSchemaProposer(hints), new ThrowingProjectionStore(), new InMemoryProjectionState());
        await new IntakeService(raw).AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""), cancellationToken: TestContext.Current.CancellationToken);

        var act = () => projector.ProjectAsync(Qc);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Data.Contains(Projector.ClearFailureDataKey).Should().BeFalse();
    }

    [Fact]
    public async Task A_document_arriving_mid_run_is_left_for_the_next_projection()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        await h.Accept("""{"lot":"L-2","qty":2}""");
        var head = await h.Raw.HeadAsync(Qc, TestContext.Current.CancellationToken);
        await h.Accept("""{"lot":"L-3","qty":3}"""); // lands after the run's head was captured

        // Intake never blocks on projection, so documents keep landing while a run streams raw. A store
        // whose stream is not snapshot-isolated will hand those later documents to the run — the port
        // promises no isolation, so the projector must bound the run itself.
        var live = new StreamsPastCapturedHead(h.Raw, head);
        var result = await new Projector(live, new HintSchemaProposer(h.Hints), h.Store, h.State).ProjectAsync(Qc, TestContext.Current.CancellationToken);

        result.Inserted.Should().Be(2, "only the documents at or below the captured head belong to this run");
        var lots = (await h.Store.QueryAsync(Table, QuerySpec.All, TestContext.Current.CancellationToken)).Select(row => row["lot"]);
        lots.Should().BeEquivalentTo(["L-1", "L-2"]);
        (await h.State.GetAsync(Qc, TestContext.Current.CancellationToken))?.Watermark.Should().Be(head,
            "the recorded watermark must describe the rows actually written, not raw's later head");
    }

    [Fact]
    public async Task The_next_projection_picks_up_what_the_previous_run_left()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");
        var head = await h.Raw.HeadAsync(Qc, TestContext.Current.CancellationToken);
        await h.Accept("""{"lot":"L-2","qty":2}""");
        var live = new StreamsPastCapturedHead(h.Raw, head);
        await new Projector(live, new HintSchemaProposer(h.Hints), h.Store, h.State).ProjectAsync(Qc, TestContext.Current.CancellationToken);

        // Bounding a run defers the straggler, it does not drop it.
        var second = await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        second.Inserted.Should().Be(2);
        (await h.State.GetAsync(Qc, TestContext.Current.CancellationToken))?.Watermark.Should().Be(new Watermark(2));
    }

    [Fact]
    public async Task The_recorded_stamp_fingerprints_the_declared_shape_not_the_augmented_table()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1","qty":1}""");

        await h.Projector.ProjectAsync(Qc, TestContext.Current.CancellationToken);

        var declared = new TableSchema(Table,
        [
            new ColumnDef("lot", ColumnType.Text, Nullable: false),
            new ColumnDef("qty", ColumnType.Integer, Nullable: true),
        ]);
        var stamp = await h.State.GetAsync(Qc, TestContext.Current.CancellationToken);
        stamp!.TableName.Should().Be(Table);
        // Status evaluation compares against the proposer's output, which never carries the system
        // columns — a stamp fingerprinting the augmented physical shape would always read as drifted.
        stamp.SchemaFingerprint.Should().Be(declared.Fingerprint());
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

    /// <summary>A projection state whose cleanup path is down — standing in for a durable state store
    /// hit by the same outage that failed the rebuild (shared connection pool).</summary>
    private sealed class ThrowingProjectionState(Exception failure) : IProjectionState
    {
        public Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromResult<ProjectionStamp?>(null);
        public Task SetProjectedAsync(FormTypeRef type, ProjectionStamp stamp, IReadOnlyList<ProjectionSkip> skips, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task<IReadOnlyList<ProjectionSkip>> GetSkipsAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromResult<IReadOnlyList<ProjectionSkip>>([]);
        public Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromException(failure);
        // The same outage that fails the cleanup fails the fallback too — the worst case, where the
        // original rebuild cause must still be what the caller sees.
        public Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromException(failure);
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
