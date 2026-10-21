using Formbase.Core.Schema;
using Formbase.Sqlite;
using Microsoft.Data.Sqlite;

namespace Formbase.Core.Tests.Contracts.Sqlite;

/// <summary>
/// The SQLite store builds each declared index with the table, columns in declared order, and the query
/// planner can use it — a rebuilt table has it again because it is built with the table, not after it.
/// </summary>
public sealed class SqliteDeclaredIndexTests : IDisposable
{
    private readonly TemporarySqliteFile _file = new();

    public void Dispose() => _file.Dispose();

    [Fact]
    public async Task A_declared_index_is_built_with_the_table_and_used_by_the_planner()
    {
        var store = new SqliteProjectionStore(_file.Database);
        var schema = new TableSchema(
            "events",
            [
                new ColumnDef("kind", ColumnType.Text, Nullable: true),
                new ColumnDef("collection", ColumnType.Text, Nullable: true),
                new ColumnDef("time_us", ColumnType.Integer, Nullable: true),
            ],
            Indexes: [new IndexDef(["kind", "collection", "time_us"]), new IndexDef(["time_us"])]);

        await store.CreateTableAsync(schema, TestContext.Current.CancellationToken);
        await store.DropTableAsync("events", TestContext.Current.CancellationToken);
        await store.CreateTableAsync(schema, TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection(_file.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        (await ColumnsOf(connection, "fb_dx_events_0")).Should().Equal("kind", "collection", "time_us");
        (await ColumnsOf(connection, "fb_dx_events_1")).Should().Equal("time_us");

        await using var plan = connection.CreateCommand();
        plan.CommandText = "EXPLAIN QUERY PLAN SELECT count(*) FROM events WHERE kind = 'commit' AND collection = 'post'";
        await using var reader = await plan.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var steps = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            steps.Add(reader.GetString(3));
        }

        steps.Should().Contain(step => step.Contains("fb_dx_events_0", StringComparison.Ordinal));
    }

    /// <summary>
    /// The planner chooses between declared indexes from statistics; a table filled by a projection has
    /// them once rows have been written, without anyone running ANALYZE.
    /// </summary>
    [Fact]
    public async Task Writing_rows_leaves_planner_statistics_for_the_declared_indexes()
    {
        var store = new SqliteProjectionStore(_file.Database);
        var schema = new TableSchema(
            "events",
            [new ColumnDef("kind", ColumnType.Text, Nullable: true), new ColumnDef("time_us", ColumnType.Integer, Nullable: true)],
            Indexes: [new IndexDef(["kind", "time_us"])]);
        await store.CreateTableAsync(schema, TestContext.Current.CancellationToken);

        await store.BulkInsertAsync(
            "events",
            [.. Enumerable.Range(0, 50).Select(i => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?> { ["kind"] = i % 2 == 0 ? "commit" : "identity", ["time_us"] = (long)i })],
            TestContext.Current.CancellationToken);

        await using var connection = new SqliteConnection(_file.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var stat = connection.CreateCommand();
        stat.CommandText = "SELECT count(*) FROM sqlite_stat1 WHERE tbl = 'events' AND idx = 'fb_dx_events_0'";
        Convert.ToInt64(await stat.ExecuteScalarAsync(TestContext.Current.CancellationToken), System.Globalization.CultureInfo.InvariantCulture)
            .Should().Be(1);
    }

    private static async Task<List<string>> ColumnsOf(SqliteConnection connection, string index)
    {
        await using var info = connection.CreateCommand();
        info.CommandText = "SELECT name FROM pragma_index_info($index) ORDER BY seqno";
        info.Parameters.AddWithValue("$index", index);
        await using var reader = await info.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        var columns = new List<string>();
        while (await reader.ReadAsync(TestContext.Current.CancellationToken))
        {
            columns.Add(reader.GetString(0));
        }

        return columns;
    }
}
