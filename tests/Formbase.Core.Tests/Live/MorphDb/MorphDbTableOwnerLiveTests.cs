using Formbase.Core.Errors;
using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Formbase.MorphDb;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// Two writers sharing one MorphDB project, each projecting the same table name: before tables
/// carried their builder's mark, the second rebuild dropped the first writer's table and filled it
/// with its own rows, and neither side saw an error. Requires Docker (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbTableOwnerLiveTests(MorphDbFixture fixture)
{
    [Fact]
    public async Task A_table_built_by_another_owner_is_refused_and_left_standing()
    {
        var table = NewTable();
        var first = Store("store-a", "writer A");
        var second = Store("store-b", "writer B");

        await first.CreateTableAsync(Schema(table), Ct);
        await first.BulkInsertAsync(table, [Row(1)], Ct);

        var refusal = await FluentActions.Awaiting(() => second.DropTableAsync(table, Ct))
            .Should().ThrowAsync<ProjectionTableOwnedElsewhereException>();
        refusal.Which.TableName.Should().Be(table);
        refusal.Which.Message.Should().Contain("writer A").And.Contain("writer B");

        var rows = await first.QueryAsync(table, new Formbase.Core.Query.QuerySpec(), Ct);
        rows.Should().ContainSingle("the refused writer must not have dropped the table");
    }

    [Fact]
    public async Task The_owner_that_built_a_table_rebuilds_it_and_the_mark_survives()
    {
        var table = NewTable();
        var store = Store("store-a", "writer A");

        await store.CreateTableAsync(Schema(table), Ct);
        await store.DropTableAsync(table, Ct);
        await store.CreateTableAsync(Schema(table), Ct);

        var info = await fixture.CreateClient().Schema.GetTableAsync(table, Ct);
        info!.Metadata.Should().Contain(MorphDbProjectionStore.OwnerKey, "store-a")
            .And.Contain(MorphDbProjectionStore.OwnerLabelKey, "writer A");
    }

    /// <summary>
    /// Tables built before marks existed carry none. Refusing them would stop every upgraded host at
    /// its first rebuild, so an unmarked table is replaced as before — and comes back marked.
    /// </summary>
    [Fact]
    public async Task An_unmarked_table_is_replaced_and_comes_back_marked()
    {
        var table = NewTable();
        await new MorphDbProjectionStore(fixture.CreateClient()).CreateTableAsync(Schema(table), Ct);

        var store = Store("store-a", "writer A");
        await store.DropTableAsync(table, Ct);
        await store.CreateTableAsync(Schema(table), Ct);

        var info = await fixture.CreateClient().Schema.GetTableAsync(table, Ct);
        info!.Metadata.Should().Contain(MorphDbProjectionStore.OwnerKey, "store-a");
    }

    [Fact]
    public async Task A_store_without_an_owner_marks_nothing_and_replaces_anything()
    {
        var table = NewTable();
        await Store("store-a", "writer A").CreateTableAsync(Schema(table), Ct);

        var unowned = new MorphDbProjectionStore(fixture.CreateClient());
        await unowned.DropTableAsync(table, Ct);
        await unowned.CreateTableAsync(Schema(table), Ct);

        var info = await fixture.CreateClient().Schema.GetTableAsync(table, Ct);
        info!.Metadata.Should().BeEmpty();
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MorphDbProjectionStore Store(string id, string label) =>
        new(fixture.CreateClient(), new FixedOwner(new ProjectionTableOwner(id, label)));

    private static string NewTable() => $"owner_{Guid.NewGuid():N}"[..20];

    private static TableSchema Schema(string table) =>
        new(table, [.. ProjectionSystemColumns.All, new ColumnDef("total", ColumnType.Integer, Nullable: true)]);

    private static Dictionary<string, object?> Row(int total)
    {
        var document = Guid.NewGuid();
        return new()
        {
            [ProjectionSystemColumns.DocumentId] = document,
            [ProjectionSystemColumns.Watermark] = 1L,
            [ProjectionSystemColumns.Record] = document.ToString("D"),
            ["total"] = total,
        };
    }

    private sealed class FixedOwner(ProjectionTableOwner owner) : IProjectionTableOwnerSource
    {
        public ValueTask<ProjectionTableOwner> GetOwnerAsync(CancellationToken cancellationToken) => new(owner);
    }
}
