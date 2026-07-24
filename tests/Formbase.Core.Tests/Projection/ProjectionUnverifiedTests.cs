using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

/// <summary>
/// The tri-state projection state (design 2026-07-23 §6, adopted §3.12-①-④): when a rebuild fails
/// and the state cleanup ALSO fails (shared connection pool), the recorded stamp may overclaim a
/// half-built table as fresh. A best-effort "unverified" mark lets a query refuse to trust it —
/// closing the C2 silent-wrong-answer window — while the projector's original cause still
/// propagates (cycle-34).
/// </summary>
public class ProjectionUnverifiedTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly ProjectionStamp Stamp = new(new Watermark(1), "qc", "fp");

    [Fact]
    public void An_unverified_stamp_evaluates_to_the_unverified_state()
    {
        var schema = new TableSchema("qc", [new ColumnDef("lot", ColumnType.Text)]);
        var status = ProjectionStatus.Evaluate(Stamp with { Verified = false }, new Watermark(1), schema);

        status.State.Should().Be(ProjectionState.Unverified,
            "a stamp whose integrity was left unconfirmed is neither trustworthy-fresh nor absent");
    }

    [Fact]
    public void A_declaration_that_moved_tables_still_reads_not_projected_over_an_unverified_stamp()
    {
        var moved = new TableSchema("qc_v2", [new ColumnDef("lot", ColumnType.Text)]);
        var status = ProjectionStatus.Evaluate(Stamp with { Verified = false }, new Watermark(1), moved);

        status.State.Should().Be(ProjectionState.NotProjected,
            "the current declaration's table was never built — that outranks the old stamp's verification");
    }

    [Fact]
    public async Task Marking_unverified_flips_an_existing_stamp_and_no_ops_when_absent()
    {
        var state = new InMemoryProjectionState();

        await state.MarkUnverifiedAsync(Qc); // no stamp yet — must be a no-op, not an insert
        (await state.GetAsync(Qc)).Should().BeNull();

        await state.SetProjectedAsync(Qc, Stamp);
        await state.MarkUnverifiedAsync(Qc);
        (await state.GetAsync(Qc))!.Verified.Should().BeFalse();
    }

    [Fact]
    public async Task A_query_over_an_unverified_projection_refuses_rather_than_serving_partial_rows()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, "qc", [new FieldHint("lot", ColumnType.Text)]));
        var store = new InMemoryProjectionStore();
        var state = new InMemoryProjectionState();
        await new IntakeService(raw).AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""));

        var proposer = new HintSchemaProposer(hints);
        await new Projector(raw, proposer, store, state).ProjectAsync(Qc);
        // Simulate the aftermath of a failed rebuild whose cleanup also failed: the table is now
        // suspect, and the state was marked unverified rather than cleared.
        await state.MarkUnverifiedAsync(Qc);

        var query = new RecordQuery(raw, proposer, store, state);
        var act = () => query.QueryAsync(Qc, QuerySpec.All);

        await act.Should().ThrowAsync<ProjectionUnverifiedException>(
            "an unconfirmed projection must not answer as if it were fresh");
    }

    [Fact]
    public async Task A_clear_failure_marks_the_state_unverified_as_a_best_effort_fallback()
    {
        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        hints.Declare(new FormTypeHints(Qc, "qc", [new FieldHint("lot", ColumnType.Text)]));
        await new IntakeService(raw).AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-1"}"""));

        // A state whose ClearAsync fails but whose MarkUnverifiedAsync works — the exact split the
        // tri-state exists for.
        var state = new ClearFailsMarkWorksState();
        var projector = new Projector(raw, new HintSchemaProposer(hints), new ThrowingOnInsertStore(), state);

        var act = () => projector.ProjectAsync(Qc);

        var thrown = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        thrown.Message.Should().Contain("bulk insert failed", "the original cause still propagates");
        state.MarkedUnverified.Should().BeTrue("clear failed, so the fallback must have marked the state unverified");
    }

    private sealed class ClearFailsMarkWorksState : IProjectionState
    {
        public bool MarkedUnverified { get; private set; }

        public Task<ProjectionStamp?> GetAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromResult<ProjectionStamp?>(null);
        public Task SetProjectedAsync(FormTypeRef type, ProjectionStamp stamp, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task ClearAsync(FormTypeRef type, CancellationToken cancellationToken = default)
            => Task.FromException(new TimeoutException("state store unreachable"));
        public Task MarkUnverifiedAsync(FormTypeRef type, CancellationToken cancellationToken = default)
        {
            MarkedUnverified = true;
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingOnInsertStore : IProjectionStore
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
