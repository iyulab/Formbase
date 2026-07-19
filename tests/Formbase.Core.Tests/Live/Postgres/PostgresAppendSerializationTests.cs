using Formbase.Core.Primitives;
using Npgsql;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// Pins what the append-path advisory lock actually buys, which the shared contract suite cannot see.
/// <para>
/// The contract suite's concurrency test asserts on <i>final</i> state — distinct watermarks, nothing
/// lost. Postgres sequences are concurrency-safe on their own, so that assertion holds with or without
/// the lock: removing the lock leaves the whole suite green (measured, cycle 21). What the lock is
/// really for is the <b>check-then-insert window</b>: two transactions both read "no such id", both
/// insert, and one dies on the primary key instead of returning idempotently. Holding the lock through
/// commit closes that window — and because the store reads at READ COMMITTED, the waiter's lookup runs
/// against a snapshot taken after the winner committed, so it sees the row and returns it.
/// </para>
/// <para>
/// <b>Not covered here.</b> The lock also makes watermark <i>assignment order equal commit order</i>,
/// without which a projection reading the head mid-flight could record a head above a document that
/// commits later and skip it forever. That failure exists only inside a transient window, and the
/// public API offers no way to hold a commit open, so it has no deterministic test — asserting it
/// would mean a probabilistic poller that can fail while the code is correct. It stays covered by
/// argument (see the type's remarks), not by test.
/// </para>
/// Requires Docker (category: Live.Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live.Postgres")]
public sealed class PostgresAppendSerializationTests
{
    private const int Racers = 8;

    private readonly PostgresFixture _fixture;

    public PostgresAppendSerializationTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Concurrent_appends_of_one_id_all_return_the_same_single_row()
    {
        var schema = "fb_append_" + Guid.NewGuid().ToString("N");
        await using var racers = new RacingStores(_fixture, schema, Racers);
        var type = FormTypeRef.Create("invoice");
        var id = DocumentId.New();

        // Every racer submits the same document id at once — the re-submission a retrying client or a
        // second instance makes. Idempotency must hold under a race, not just in sequence.
        var stored = await racers.RaceAsync(store => store.AppendAsync(type, id, DocumentBody.Parse("""{"n":1}""")));

        stored.Select(document => document.Watermark.Value).Distinct()
            .Should().ContainSingle("every racer must observe the one document that won, not its own insert");
        (await CountRowsAsync(schema, id)).Should().Be(1, "a duplicate id must never create a second row");
        (await SequenceValueAsync(schema)).Should().Be(1, "the losing racers must not burn watermarks");
    }

    private async Task<int> CountRowsAsync(string schema, DocumentId id)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT count(*) FROM "{schema}".raw_documents WHERE id = @id""", connection);
        command.Parameters.AddWithValue("id", id.Value);
        return (int)(long)(await command.ExecuteScalarAsync())!;
    }

    private async Task<long> SequenceValueAsync(string schema)
    {
        await using var connection = await _fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            $"""SELECT last_value FROM "{schema}".raw_watermark_seq""", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }
}
