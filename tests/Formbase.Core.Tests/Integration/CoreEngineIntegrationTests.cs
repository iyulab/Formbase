using Formbase.Core;
using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Integration;

/// <summary>
/// End-to-end scenarios over the whole in-memory engine. These prove the design's core claims without
/// any external database.
/// </summary>
public class CoreEngineIntegrationTests
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("품질검사의뢰서");
    private const string Table = "qc_requests";

    private sealed class Engine
    {
        public InMemoryRawStore Raw { get; } = new();
        public InMemoryFieldHintSource Hints { get; } = new();
        public InMemoryProjectionStore Inner { get; } = new();
        public ToggleableProjectionStore Store { get; }
        public InMemoryProjectionState State { get; } = new();
        public FormbaseEngine Core { get; }

        public Engine()
        {
            Store = new ToggleableProjectionStore(Inner);
            var proposer = new HintSchemaProposer(Hints);
            var projector = new Projector(Raw, proposer, Store, State);
            var recordQuery = new RecordQuery(Raw, proposer, Store, State);
            Core = new FormbaseEngine(new IntakeService(Raw), Raw, projector, recordQuery, State);
        }

        public void DeclareHints() => Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));

        public Task<DocumentId> Accept(int n) =>
            Core.AcceptAsync(Qc, DocumentBody.Parse($$"""{"lot":"L-{{n}}","qty":{{n}}}"""));
    }

    [Fact]
    public async Task Raw_first_roundtrip_data_before_structure()
    {
        var e = new Engine();

        // 1) Accept documents with NO declaration anywhere.
        var ids = new List<DocumentId>();
        for (var n = 1; n <= 10; n++)
        {
            ids.Add(await e.Accept(n));
        }

        // 2) Record queries are impossible — but as NotProjected, never an empty result.
        await FluentActions.Awaiting(() => e.Core.QueryAsync(Qc, QuerySpec.All))
            .Should().ThrowAsync<NotProjectedException>();

        // 3) The human's question still works: every document is retrievable.
        foreach (var id in ids)
        {
            (await e.Core.GetDocumentAsync(id)).Should().NotBeNull();
        }
        (await e.Core.GetProjectionStatusAsync(Qc)).State.Should().Be(ProjectionState.NotProjected);

        // 4) Declare structure after the fact and project.
        e.DeclareHints();
        var projection = await e.Core.ProjectAsync(Qc);
        projection.Inserted.Should().Be(10);

        // 5) Now the system's question is answerable.
        var result = await e.Core.QueryAsync(Qc, QuerySpec.All);
        result.Rows.Should().HaveCount(10);
        result.Stale.Should().BeFalse();
        (await e.Core.GetProjectionStatusAsync(Qc)).State.Should().Be(ProjectionState.Projected);
    }

    [Fact]
    public async Task Reprojection_absorbs_new_documents_and_is_idempotent()
    {
        var e = new Engine();
        e.DeclareHints();
        for (var n = 1; n <= 10; n++)
        {
            await e.Accept(n);
        }
        await e.Core.ProjectAsync(Qc);

        // Append more → projection becomes stale.
        for (var n = 11; n <= 15; n++)
        {
            await e.Accept(n);
        }
        (await e.Core.GetProjectionStatusAsync(Qc)).State.Should().Be(ProjectionState.Stale);
        (await e.Core.QueryAsync(Qc, QuerySpec.All)).Stale.Should().BeTrue();

        // Re-project → current again, all 15 rows, no duplicates.
        var second = await e.Core.ProjectAsync(Qc);
        second.Inserted.Should().Be(15);
        (await e.Core.GetProjectionStatusAsync(Qc)).State.Should().Be(ProjectionState.Projected);

        // Re-projecting unchanged raw is idempotent.
        var third = await e.Core.ProjectAsync(Qc);
        third.Inserted.Should().Be(15);
        (await e.Core.QueryAsync(Qc, QuerySpec.All)).Rows.Should().HaveCount(15);
    }

    [Fact]
    public async Task A_backing_store_outage_isolates_only_record_queries()
    {
        var e = new Engine();
        e.DeclareHints();
        var id = await e.Accept(1);
        await e.Core.ProjectAsync(Qc);

        // Backing store goes down.
        e.Store.IsAvailable = false;

        // Intake keeps working (raw store is formbase-owned)...
        var duringOutage = await e.Core.AcceptAsync(Qc, DocumentBody.Parse("""{"lot":"L-2","qty":2}"""));
        duringOutage.Should().NotBe(default(DocumentId));
        // ...document reads keep working...
        (await e.Core.GetDocumentAsync(id)).Should().NotBeNull();
        // ...only record queries fail, and distinctly.
        await FluentActions.Awaiting(() => e.Core.QueryAsync(Qc, QuerySpec.All))
            .Should().ThrowAsync<ProjectionUnavailableException>();

        // Recovery needs no re-projection — the table survived.
        e.Store.IsAvailable = true;
        (await e.Core.QueryAsync(Qc, QuerySpec.All)).Rows.Should().HaveCount(1);
    }

    [Fact]
    public async Task A_failed_rebuild_leaves_records_unavailable_but_documents_intact()
    {
        var e = new Engine();
        e.DeclareHints();
        var id = await e.Accept(1);
        await e.Core.ProjectAsync(Qc);
        (await e.Core.QueryAsync(Qc, QuerySpec.All)).Rows.Should().HaveCount(1);

        // A rebuild that fails after the drop (store unavailable mid-projection).
        e.Store.IsAvailable = false;
        await FluentActions.Awaiting(() => e.Core.ProjectAsync(Qc)).Should().ThrowAsync<Exception>();
        e.Store.IsAvailable = true;

        // The record path honestly reports NotProjected (not empty, not stale data)...
        (await e.Core.GetProjectionStatusAsync(Qc)).State.Should().Be(ProjectionState.NotProjected);
        await FluentActions.Awaiting(() => e.Core.QueryAsync(Qc, QuerySpec.All)).Should().ThrowAsync<NotProjectedException>();
        // ...while the raw source of truth is untouched.
        (await e.Core.GetDocumentAsync(id)).Should().NotBeNull();

        // And a fresh projection fully recovers.
        (await e.Core.ProjectAsync(Qc)).Inserted.Should().Be(1);
        (await e.Core.QueryAsync(Qc, QuerySpec.All)).Rows.Should().HaveCount(1);
    }
}
