using Formbase.Core.Projection;
using Formbase.Core.Schema;
using Formbase.MorphDb;
using MorphDB.Client.Models;

namespace Formbase.Core.Tests.Live.MorphDb;

/// <summary>
/// A rebuild refreshes a MorphDB table in place: the rows go and the columns follow the declaration, but
/// the table stays the same table — so a row-level security policy bound to it survives. Dropping and
/// recreating it removed the policy without a word and left every row readable. Requires Docker
/// (category: Live).
/// </summary>
[Collection(MorphDbCollection.Name)]
[Trait("Category", "Live.MorphDb")]
public sealed class MorphDbRebuildInPlaceLiveTests(MorphDbFixture fixture)
{
    private static TableSchema Schema(string table, params ColumnDef[] columns) =>
        new(table, [.. ProjectionSystemColumns.All, .. columns], Indexes: [new IndexDef(["inspector"])]);

    private async Task<TableInfo> ProjectedWithPolicyAsync(string table)
    {
        var client = fixture.CreateClient();
        var store = new MorphDbProjectionStore(client);
        await store.ResetTableAsync(Schema(table,
            new ColumnDef("inspector", ColumnType.Text, Nullable: true),
            new ColumnDef("result", ColumnType.Text, Nullable: true)), Ct);
        await client.Data.InsertAsync(table, new Dictionary<string, object?>
        {
            [ProjectionSystemColumns.DocumentId] = Guid.NewGuid(),
            [ProjectionSystemColumns.Watermark] = 1L,
            [ProjectionSystemColumns.Record] = "r1",
            ["inspector"] = "alice",
            ["result"] = "pass",
        }, Ct);
        await client.Security.CreatePolicyAsync(new CreateSecurityPolicyRequest
        {
            Name = "own_rows",
            TableName = table,
            PolicyType = PolicyType.Select,
            Expression = "inspector = {{user_id}}",
        }, Ct);
        return (await client.Schema.GetTableAsync(table, Ct))!;
    }

    [Fact]
    public async Task A_rebuild_keeps_the_table_and_the_policy_bound_to_it_and_brings_the_columns_to_the_declaration()
    {
        var table = $"rb_{Guid.NewGuid():N}"[..20];
        var before = await ProjectedWithPolicyAsync(table);
        var client = fixture.CreateClient();

        // The declaration changes: a column is added, one changes type, one is kept as it was.
        await new MorphDbProjectionStore(client).ResetTableAsync(Schema(table,
            new ColumnDef("inspector", ColumnType.Text, Nullable: true),
            new ColumnDef("result", ColumnType.Integer, Nullable: true),
            new ColumnDef("note", ColumnType.Text, Nullable: true)), Ct);

        var after = (await client.Schema.GetTableAsync(table, Ct))!;
        after.Id.Should().Be(before.Id, "the table is refreshed, not replaced");
        after.Columns.Single(c => c.Name == "inspector").Id.Should().Be(before.Columns.Single(c => c.Name == "inspector").Id,
            "a column whose shape is unchanged is kept, so a policy naming it keeps naming it");
        after.Columns.Single(c => c.Name == "result").Type.Should().Be("biginteger");
        after.Columns.Should().Contain(c => c.Name == "note");
        after.Indexes.Where(i => i.Name.StartsWith("fb_dx_", StringComparison.Ordinal)).Select(i => string.Join(',', i.Columns))
            .Should().Equal("inspector");
        (await client.Security.GetPoliciesAsync(table, Ct)).Select(p => p.Name).Should().Equal("own_rows");
        (await client.Data.QueryAsync(table, new QueryRequest(), Ct)).Data.Should().BeEmpty("a rebuild refills an empty table");
    }

    [Fact]
    public async Task A_rebuild_that_would_change_the_column_a_policy_names_fails_rather_than_open_the_table()
    {
        var table = $"rp_{Guid.NewGuid():N}"[..20];
        await ProjectedWithPolicyAsync(table);
        var client = fixture.CreateClient();

        var act = () => new MorphDbProjectionStore(client).ResetTableAsync(Schema(table,
            new ColumnDef("inspector", ColumnType.Integer, Nullable: true),
            new ColumnDef("result", ColumnType.Text, Nullable: true)), Ct);

        await act.Should().ThrowAsync<Exception>();
        (await client.Security.GetPoliciesAsync(table, Ct)).Select(p => p.Name).Should().Equal(
            ["own_rows"], "the policy is still there: the table did not become readable");
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
