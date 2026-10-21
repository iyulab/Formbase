using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// The MorphDB store builds each declared index with the table, columns in declared order, and a rebuilt
/// table has them again. Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbDeclaredIndexLiveTests(MorphDbFixture fixture)
{
    [Fact]
    public async Task Declared_indexes_are_built_with_the_table_and_again_when_it_is_rebuilt()
    {
        var table = $"dx_{Guid.NewGuid():N}"[..20];
        var store = new MorphDbProjectionStore(fixture.CreateClient());
        var schema = new TableSchema(
            table,
            [
                .. ProjectionSystemColumns.All,
                new ColumnDef("kind", ColumnType.Text, Nullable: true),
                new ColumnDef("time_us", ColumnType.Integer, Nullable: true),
            ],
            Indexes: [new IndexDef(["kind", "time_us"]), new IndexDef(["time_us"])]);

        await store.CreateTableAsync(schema, Ct);
        await store.DropTableAsync(table, Ct);
        await store.CreateTableAsync(schema, Ct);

        var info = await fixture.CreateClient().Schema.GetTableAsync(table, Ct);
        info!.Indexes.Where(i => i.Name.StartsWith("fb_dx_", StringComparison.Ordinal))
            .OrderBy(i => i.Name, StringComparer.Ordinal)
            .Select(i => string.Join(',', i.Columns))
            .Should().Equal("kind,time_us", "time_us");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
