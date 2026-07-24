using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Projection;

public class ProjectionTriggerTests
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

        public WatermarkLagTrigger Trigger(long lagThreshold = 1) =>
            new(Raw, new HintSchemaProposer(Hints), State, lagThreshold);

        public ProjectionSupervisor Supervisor(long lagThreshold = 1) =>
            new(Trigger(lagThreshold), Projector);

        public void DeclareQcHints(string table = Table) => Hints.Declare(new FormTypeHints(Qc, table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
        ]));

        public Task Accept(string json) => Intake.AcceptAsync(Qc, DocumentBody.Parse(json));
    }

    [Fact]
    public async Task Does_not_fire_when_nothing_proposes_a_schema()
    {
        var h = new Harness();
        await h.Accept("""{"lot":"L-1"}""");

        var decision = await h.Trigger().EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeFalse("projecting without a proposable schema is a no-op");
        decision.Reason.Should().Be(ProjectionTriggerReason.None);
    }

    [Fact]
    public async Task Does_not_fire_for_a_declared_type_with_no_documents()
    {
        var h = new Harness();
        h.DeclareQcHints();

        var decision = await h.Trigger().EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeFalse("there is nothing to project yet");
    }

    [Fact]
    public async Task Fires_the_first_projection_when_documents_exist()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");

        var decision = await h.Trigger().EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeTrue();
        decision.Reason.Should().Be(ProjectionTriggerReason.FirstProjection);
    }

    [Fact]
    public async Task Does_not_fire_when_the_projection_is_current()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);

        var decision = await h.Trigger().EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeFalse();
        decision.Status.State.Should().Be(ProjectionState.Projected);
    }

    [Fact]
    public async Task Fires_immediately_on_shape_drift_regardless_of_threshold()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);
        h.Hints.Declare(new FormTypeHints(Qc, Table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));

        var decision = await h.Trigger(lagThreshold: 100).EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeTrue("a redeclared shape serves wrong-shaped rows until rebuilt");
        decision.Reason.Should().Be(ProjectionTriggerReason.ShapeDrift);
    }

    [Fact]
    public async Task Fires_when_the_declaration_moved_to_a_new_table()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);
        h.DeclareQcHints(table: "qc_v2");

        var decision = await h.Trigger(lagThreshold: 100).EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeTrue("the current declaration's table was never built");
        decision.Reason.Should().Be(ProjectionTriggerReason.FirstProjection);
    }

    [Fact]
    public async Task Holds_below_the_lag_threshold()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);
        await h.Accept("""{"lot":"L-2"}""");
        await h.Accept("""{"lot":"L-3"}""");

        var decision = await h.Trigger(lagThreshold: 3).EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeFalse("2 documents behind is below the threshold of 3");
        decision.Status.State.Should().Be(ProjectionState.Stale, "holding is a policy choice, not ignorance");
    }

    [Fact]
    public async Task Fires_at_the_lag_threshold()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);
        await h.Accept("""{"lot":"L-2"}""");
        await h.Accept("""{"lot":"L-3"}""");

        var decision = await h.Trigger(lagThreshold: 2).EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeTrue();
        decision.Reason.Should().Be(ProjectionTriggerReason.DataLag);
    }

    [Fact]
    public async Task The_default_threshold_fires_on_a_single_new_document()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);
        await h.Accept("""{"lot":"L-2"}""");

        var decision = await h.Trigger().EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeTrue();
        decision.Reason.Should().Be(ProjectionTriggerReason.DataLag);
    }

    [Fact]
    public void A_threshold_below_one_is_rejected()
    {
        var h = new Harness();

        var act = () => h.Trigger(lagThreshold: 0);

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task Fires_to_restore_an_unverified_projection()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);
        // A failed rebuild's fallback left the projection unverified: the trigger must rebuild it,
        // not hold — otherwise a suspect projection is never repaired by the automation.
        await h.State.MarkUnverifiedAsync(Qc);

        var decision = await h.Trigger().EvaluateAsync(Qc);

        decision.ShouldProject.Should().BeTrue();
        decision.Reason.Should().Be(ProjectionTriggerReason.Unverified);
    }

    [Fact]
    public async Task The_supervisor_restores_an_unverified_projection_to_verified()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        await h.Projector.ProjectAsync(Qc);
        await h.State.MarkUnverifiedAsync(Qc);

        var run = await h.Supervisor().RunOnceAsync(Qc);

        run.Decision.ShouldProject.Should().BeTrue();
        run.Projection.Should().NotBeNull();
        (await h.State.GetAsync(Qc))!.Verified.Should().BeTrue("a rebuild restores verified integrity");
    }

    [Fact]
    public async Task The_supervisor_projects_when_due_and_converges()
    {
        var h = new Harness();
        h.DeclareQcHints();
        await h.Accept("""{"lot":"L-1"}""");
        var supervisor = h.Supervisor();

        var first = await supervisor.RunOnceAsync(Qc);
        var second = await supervisor.RunOnceAsync(Qc);

        first.Decision.ShouldProject.Should().BeTrue();
        first.Projection.Should().NotBeNull();
        first.Projection!.Inserted.Should().Be(1);
        second.Decision.ShouldProject.Should().BeFalse("one run brings the projection current");
        second.Projection.Should().BeNull();
    }
}
