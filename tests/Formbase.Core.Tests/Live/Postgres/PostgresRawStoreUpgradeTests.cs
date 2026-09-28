using Formbase.Core.Primitives;
using Formbase.Postgres;
using Npgsql;

namespace Formbase.Core.Tests.Live.Postgres;

/// <summary>
/// A schema written before record keys existed: <c>body</c> is NOT NULL and there is no
/// <c>record_key</c>. <c>CREATE TABLE IF NOT EXISTS</c> leaves such a table as it was, so these pin that
/// the store upgrades it — once, even when several instances reach it together — and that the documents
/// it already held read back unchanged.
/// Requires Docker (category: Live.Postgres).
/// </summary>
[Collection(PostgresCollection.Name)]
[Trait("Category", "Live.Postgres")]
public sealed class PostgresRawStoreUpgradeTests
{
    private static readonly FormTypeRef Invoice = FormTypeRef.Create("invoice");

    private readonly PostgresFixture _fixture;

    public PostgresRawStoreUpgradeTests(PostgresFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_document_from_before_the_upgrade_reads_back_as_a_record_of_its_own()
    {
        var (schema, oldId) = await CreateEarlierSchemaAsync();
        var store = new PostgresRawStore(_fixture.DataSource, schema);

        var old = await store.GetAsync(DocumentId.From(oldId), TestContext.Current.CancellationToken);

        old.Should().NotBeNull();
        old!.Key.Should().BeNull();
        old.IsRetirement.Should().BeFalse();
        old.Body!.Root.GetProperty("n").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task An_upgraded_schema_takes_keyed_appends_and_retirements()
    {
        var (schema, _) = await CreateEarlierSchemaAsync();
        var store = new PostgresRawStore(_fixture.DataSource, schema);
        var key = RecordKey.Create("a");

        var keyed = await store.AppendAsync(Invoice, DocumentId.New(), DocumentBody.Parse("""{"n":2}"""), key, TestContext.Current.CancellationToken);
        var retirement = await store.RetireAsync(Invoice, DocumentId.New(), key, TestContext.Current.CancellationToken);

        keyed.Watermark.Should().Be(new Watermark(2), "positions continue after the documents the schema already held");
        (await store.GetAsync(retirement.Id, TestContext.Current.CancellationToken))!.IsRetirement.Should().BeTrue();
    }

    [Fact]
    public async Task Instances_reaching_an_earlier_schema_together_upgrade_it_once()
    {
        var (schema, _) = await CreateEarlierSchemaAsync();
        await using var racers = new RacingStores(_fixture, schema, 8);

        var retirements = await racers.RaceAsync(store =>
            store.RetireAsync(Invoice, DocumentId.New(), RecordKey.Create("a")));

        retirements.Select(r => r.Watermark.Value).Should().OnlyHaveUniqueItems();
        retirements.Should().OnlyContain(r => r.IsRetirement);
    }

    private async Task<(string Schema, Guid OldId)> CreateEarlierSchemaAsync()
    {
        var schema = "fb_upgrade_" + Guid.NewGuid().ToString("N");
        var oldId = Guid.NewGuid();
        await using var connection = await _fixture.DataSource.OpenConnectionAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand(
            $$"""
            CREATE SCHEMA "{{schema}}";
            CREATE SEQUENCE "{{schema}}".raw_watermark_seq AS bigint START 1 MINVALUE 1;
            CREATE TABLE "{{schema}}".raw_documents (
                id uuid PRIMARY KEY,
                form_type text NOT NULL,
                body jsonb NOT NULL,
                watermark bigint NOT NULL UNIQUE,
                appended_at timestamptz NOT NULL
            );
            INSERT INTO "{{schema}}".raw_documents (id, form_type, body, watermark, appended_at)
            VALUES (@id, 'invoice', '{"n":1}'::jsonb, nextval('"{{schema}}".raw_watermark_seq'), now());
            """,
            connection);
        command.Parameters.AddWithValue("id", oldId);
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        return (schema, oldId);
    }
}
