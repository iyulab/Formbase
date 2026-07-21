using Formbase.Core;
using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.MorphDb;
using Formbase.Postgres;
using Npgsql;

namespace Formbase.Core.Tests.Live.Durable;

/// <summary>
/// The guarantee the durable profile exists for: a process that comes back finds its projection.
/// <para>
/// Engine A intakes, declares, and projects. Engine B is assembled from scratch over the same schema and
/// the same MorphDB — sharing no in-process state with A — and must answer the same query. Before the
/// durable state and hint stores existed, B threw <c>NotProjectedException</c>.
/// </para>
/// Requires Docker and both live gates (category: Live.Durable).
/// </summary>
[Collection(DurableCollection.Name)]
[Trait("Category", "Live.Durable")]
public sealed class RestartSurvivalTests
{
    private readonly DurableFixture _fixture;

    public RestartSurvivalTests(DurableFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_restarted_engine_still_answers_the_query()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var schema = "fb_restart_" + suffix;
        var type = FormTypeRef.Create($"qc_{suffix}");
        var table = $"qc_{suffix}";

        // --- process 1 -------------------------------------------------------------------------
        await using (var firstSource = _fixture.CreateIndependentDataSource())
        {
            var first = BuildDurableEngine(firstSource, schema, out var hints);

            await first.AcceptAsync(type, DocumentBody.Parse("""{"serial":"A-1"}"""));
            await first.AcceptAsync(type, DocumentBody.Parse("""{"serial":"A-2"}"""));
            await hints.DeclareAsync(new FormTypeHints(type, table,
                [new FieldHint("serial", ColumnType.Text, Nullable: false)]));

            var projected = await first.ProjectAsync(type);
            projected.Projected.Should().BeTrue();
        }

        // --- process 2: nothing in common but the two databases ---------------------------------
        await using var secondSource = _fixture.CreateIndependentDataSource();
        var second = BuildDurableEngine(secondSource, schema, out _);

        // ProjectionState has three members: NotProjected, Projected, Stale. "Projected" is the
        // current-and-caught-up one; there is no member called Current.
        var status = await second.GetProjectionStatusAsync(type);
        status.State.Should().Be(ProjectionState.Projected);

        var result = await second.QueryAsync(type, QuerySpec.All);
        result.Stale.Should().BeFalse();
        result.Rows.Should().HaveCount(2);
    }

    /// <summary>Assembles a fully durable engine: Postgres raw store, state, and hints; MorphDB projection.</summary>
    private FormbaseEngine BuildDurableEngine(NpgsqlDataSource dataSource, string schema, out PostgresFieldHintSource hints)
    {
        var raw = new PostgresRawStore(dataSource, schema);
        var state = new PostgresProjectionState(dataSource, schema);
        hints = new PostgresFieldHintSource(dataSource, schema);
        var store = new MorphDbProjectionStore(_fixture.CreateMorphDbClient());
        var proposer = new HintSchemaProposer(hints);

        return new FormbaseEngine(
            new IntakeService(raw),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state);
    }
}
