using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Formbase.Postgres;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// Pins the guarantee that made the shared bootstrap necessary: three <i>different</i> store types
/// reaching one fresh schema at the same moment must all succeed.
/// <para>
/// Each store runs <c>CREATE SCHEMA IF NOT EXISTS</c>, which is not atomic against the catalog. They
/// only serialize because the advisory lock key is derived from the schema name, so all three take the
/// same lock. Give any of them its own key and this test fails on a duplicate-object error.
/// </para>
/// Requires Docker (category: Live.Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live.Postgres")]
public sealed class PostgresMixedColdStartRaceTests
{
    private readonly PostgresFixture _fixture;

    public PostgresMixedColdStartRaceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Different_store_types_cold_starting_together_all_succeed()
    {
        var schema = "fb_mixed_" + Guid.NewGuid().ToString("N");
        var type = FormTypeRef.Create("invoice");

        // Separate pools: stores over one pool would still share nothing in-process, but separate pools
        // are the closest a single test host gets to separate processes.
        await using var rawSource = _fixture.CreateIndependentDataSource();
        await using var stateSource = _fixture.CreateIndependentDataSource();
        await using var hintSource = _fixture.CreateIndependentDataSource();

        using var raw = new PostgresRawStore(rawSource, schema);
        using var state = new PostgresProjectionState(stateSource, schema);
        using var hints = new PostgresFieldHintSource(hintSource, schema);

        var act = async () => await Task.WhenAll(
            raw.HeadAsync(type),
            state.SetProjectedAsync(type, new ProjectionStamp(new Watermark(1), "invoice_table", "fp-race")),
            hints.DeclareAsync(new FormTypeHints(type, "invoice_table", [new FieldHint("n", ColumnType.Integer)])));

        await act.Should().NotThrowAsync();

        (await state.GetAsync(type))?.Watermark.Should().Be(new Watermark(1));
        (await hints.GetHintsAsync(type)).Should().NotBeNull();
    }
}
