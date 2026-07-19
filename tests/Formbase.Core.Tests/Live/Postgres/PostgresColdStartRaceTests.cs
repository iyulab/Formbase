using Formbase.Core.Primitives;
using Formbase.Postgres;
using Npgsql;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// Pins the cold-start guarantee: many instances reaching a fresh schema at once must all succeed.
/// <para>
/// Each store gates its own initialization with a private <c>SemaphoreSlim</c>, which does nothing
/// across instances (see <see cref="RacingStores"/>) — so the only thing standing between concurrent
/// cold starts and a failure is the schema-scoped advisory lock around the DDL. Remove that lock and
/// these tests fail: <c>CREATE ... IF NOT EXISTS</c> is not atomic against the catalog, so racing
/// creators collide on a duplicate-object error (verified by mutation, cycle 20).
/// </para>
/// Requires Docker (category: Live.Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live.Postgres")]
public sealed class PostgresColdStartRaceTests
{
    private const int Racers = 8;

    private readonly PostgresFixture _fixture;

    public PostgresColdStartRaceTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Concurrent_cold_start_creates_the_schema_exactly_once()
    {
        var schema = NewSchema();
        await using var racers = new RacingStores(_fixture, schema, Racers);

        // A read is enough to trigger initialization, so this isolates the DDL race from the append path.
        var heads = await racers.RaceAsync(store => store.HeadAsync(FormTypeRef.Create("invoice")));

        heads.Should().AllSatisfy(head => head.Value.Should().Be(0), "an empty store has no watermark yet");
        (await CountTablesAsync(schema)).Should().Be(1, "the racing creators must converge on one table");
    }

    [Fact]
    public async Task Concurrent_cold_start_with_appends_assigns_unique_watermarks()
    {
        var schema = NewSchema();
        await using var racers = new RacingStores(_fixture, schema, Racers);
        var type = FormTypeRef.Create("invoice");

        // Initialization and the first append collide in the same window — the DDL lock and the append
        // lock are the same key, so this also proves the two paths do not deadlock against each other.
        var appended = await racers.RaceAsync(store =>
            store.AppendAsync(type, DocumentId.New(), DocumentBody.Parse("""{"n":1}""")));

        appended.Select(document => document.Watermark.Value)
            .Should().BeEquivalentTo(Enumerable.Range(1, Racers).Select(n => (long)n),
                "each racer consumes exactly one watermark from the shared sequence");
    }

    private async Task<int> CountTablesAsync(string schema)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM information_schema.tables WHERE table_schema = @s AND table_name = 'raw_documents'",
            connection);
        command.Parameters.AddWithValue("s", schema);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private static string NewSchema() => "fb_race_" + Guid.NewGuid().ToString("N");
}
