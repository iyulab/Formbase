using Formbase.Core;
using Formbase.Core.Errors;
using Formbase.Core.InMemory;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live;

/// <summary>
/// The flagship end-to-end scenario against a real MorphDB projection store: raw-first intake,
/// then structure, then queryable records. Everything but the projection store is in-process;
/// the projection lands in a real database. Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live")]
public sealed class MorphDbEngineLiveTests
{
    private readonly MorphDbFixture _fixture;

    public MorphDbEngineLiveTests(MorphDbFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Raw_first_roundtrip_against_a_real_morphdb()
    {
        // Unique form type + table per run so repeated runs against a persistent DB don't collide.
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var qc = FormTypeRef.Create($"qc_{suffix}");
        var table = $"qc_{suffix}";

        var raw = new InMemoryRawStore();
        var hints = new InMemoryFieldHintSource();
        var state = new InMemoryProjectionState();
        var store = new MorphDbProjectionStore(_fixture.CreateClient());
        var proposer = new HintSchemaProposer(hints);
        var engine = new FormbaseEngine(
            new IntakeService(raw),
            raw,
            new Projector(raw, proposer, store, state),
            new RecordQuery(raw, proposer, store, state),
            state);

        // 1) Accept without any declaration.
        for (var n = 1; n <= 5; n++)
        {
            await engine.AcceptAsync(qc, DocumentBody.Parse($$"""{"lot":"L-{{n}}","qty":{{n}}}"""));
        }

        // 2) Records not yet queryable — NotProjected, not empty.
        await FluentActions.Awaiting(() => engine.QueryAsync(qc, QuerySpec.All))
            .Should().ThrowAsync<NotProjectedException>();

        // 3) Declare structure and project into the real database.
        hints.Declare(new FormTypeHints(qc, table,
        [
            new FieldHint("lot", ColumnType.Text, Nullable: false),
            new FieldHint("qty", ColumnType.Integer, Nullable: true),
        ]));
        var projection = await engine.ProjectAsync(qc);
        projection.Inserted.Should().Be(5);

        // 4) Now the system's question is answerable, from real MorphDB rows.
        var all = await engine.QueryAsync(qc, QuerySpec.All);
        all.Rows.Should().HaveCount(5);
        all.Stale.Should().BeFalse();

        // 5) An equality filter (int coerced to the bigint column) round-trips through MorphDB.
        var filtered = await engine.QueryAsync(qc, new QuerySpec(
            Filters: new Dictionary<string, object?> { ["qty"] = 3 }));
        filtered.Rows.Should().ContainSingle();
        filtered.Rows[0]["lot"].Should().Be("L-3");

        // Cleanup.
        await store.DropTableAsync(table);
    }
}
