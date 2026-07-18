using Formbase.Core.Ports;
using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Contracts;

/// <summary>
/// The behavioral contract every <see cref="IProjectionStore"/> must honor. Held implementation-agnostic:
/// the exact exception types differ (in-memory throws <see cref="InvalidOperationException"/>, a real
/// MorphDB adapter throws its own conflict/not-found types), so the throwing cases assert only that some
/// error surfaces. Paging assertions are order-independent because the port promises no ordering.
/// </summary>
public abstract class ProjectionStoreContractTests
{
    protected abstract IProjectionStore CreateStore();

    // Unique per test instance so live stores sharing one database don't collide across tests.
    protected string TableName { get; } = "t_" + Guid.NewGuid().ToString("N");

    private TableSchema Schema() =>
        new(TableName, [new ColumnDef("k", ColumnType.Text), new ColumnDef("v", ColumnType.Integer)]);

    private static IReadOnlyDictionary<string, object?> Row(string k, long v) =>
        new Dictionary<string, object?> { ["k"] = k, ["v"] = v };

    [Fact]
    public async Task Create_then_exists_is_true_and_drop_makes_it_false()
    {
        var store = CreateStore();

        await store.CreateTableAsync(Schema());
        (await store.TableExistsAsync(TableName)).Should().BeTrue();

        await store.DropTableAsync(TableName);
        (await store.TableExistsAsync(TableName)).Should().BeFalse();
    }

    [Fact]
    public async Task Drop_is_idempotent()
    {
        var store = CreateStore();

        var act = () => store.DropTableAsync(TableName);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task Create_fails_when_the_table_already_exists()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema());

        var act = () => store.CreateTableAsync(Schema());

        await act.Should().ThrowAsync<Exception>();
        await store.DropTableAsync(TableName);
    }

    [Fact]
    public async Task Insert_into_a_missing_table_fails()
    {
        var store = CreateStore();

        var act = () => store.BulkInsertAsync(TableName, [Row("a", 1)]);

        await act.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Query_returns_inserted_rows()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema());
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2)]);

        var rows = await store.QueryAsync(TableName, QuerySpec.All);

        rows.Should().HaveCount(2);
        await store.DropTableAsync(TableName);
    }

    [Fact]
    public async Task Query_applies_equality_filters()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema());
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("a", 3)]);

        var rows = await store.QueryAsync(TableName, new QuerySpec(
            Filters: new Dictionary<string, object?> { ["k"] = "a" }));

        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(r => Equals(r["k"], "a"));
        await store.DropTableAsync(TableName);
    }

    [Fact]
    public async Task Query_limit_caps_the_row_count()
    {
        var store = CreateStore();
        await store.CreateTableAsync(Schema());
        await store.BulkInsertAsync(TableName, [Row("a", 1), Row("b", 2), Row("c", 3), Row("d", 4)]);

        var rows = await store.QueryAsync(TableName, new QuerySpec(Limit: 2));

        rows.Should().HaveCount(2);
        await store.DropTableAsync(TableName);
    }
}
