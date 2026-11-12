using System.Text.Json;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// A JSON field is projected as the text of its JSON (<c>DocumentMapper</c> hands the column the raw text). Whether the store
/// keeps that as the JSON value it spells or as a JSON string holding that text — a value that reads back as a string and
/// compares as one — is the store's to get right, and a unit test cannot tell. Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbJsonColumnLiveTests(MorphDbFixture fixture)
{
    [Fact]
    public async Task A_json_field_reads_back_as_the_json_it_was_filed_as_and_not_as_a_string_holding_it()
    {
        var store = new MorphDbProjectionStore(fixture.CreateClient());
        var table = "json_" + Guid.NewGuid().ToString("N")[..16];
        await store.CreateTableAsync(new TableSchema(table, [new ColumnDef("k", ColumnType.Text), new ColumnDef("spec", ColumnType.Jsonb, Nullable: true)]), TestContext.Current.CancellationToken);
        try
        {
            await store.BulkInsertAsync(table, [new Dictionary<string, object?> { ["k"] = "a", ["spec"] = """{"size":3,"tags":["x","y"]}""" }], TestContext.Current.CancellationToken);

            var row = Assert.Single(await store.QueryAsync(table, QuerySpec.All, TestContext.Current.CancellationToken));

            var text = row["spec"] switch
            {
                JsonElement element => element.GetRawText(),
                string raw => raw,
                var other => System.Text.Json.JsonSerializer.Serialize(other),
            };
            using var parsed = JsonDocument.Parse(text);
            parsed.RootElement.ValueKind.Should().Be(JsonValueKind.Object, $"the column read back as {text}");
            parsed.RootElement.GetProperty("size").GetInt32().Should().Be(3);
        }
        finally
        {
            await store.DropTableAsync(table, TestContext.Current.CancellationToken);
        }
    }
}
