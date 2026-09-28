using Formbase.Core.InMemory;
using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.DependencyInjection;

/// <summary>
/// The single-machine composition the SQLite adapter exists for: the core engine, a raw store, and the
/// projection side in one file — no database server. The engine answers queries and aggregates through
/// it exactly as through any other store.
/// </summary>
public sealed class SqliteDiTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"formbase-di-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task AddSqliteProjection_resolves_an_engine_that_projects_queries_and_counts_into_one_file()
    {
        var services = new ServiceCollection();
        services.AddFormbaseCore();
        services.AddSingleton<IRawStore, InMemoryRawStore>();
        services.AddSqliteProjection($"Data Source={_path}");

        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var hints = provider.GetRequiredService<SqliteFieldHintSource>();

        var reports = FormTypeRef.Create("bug-report");
        await engine.AcceptAsync(reports, DocumentBody.Parse("""{"title":"Crash on save","severity":"high","filed":"2026-09-02T10:00:00Z"}"""), cancellationToken: TestContext.Current.CancellationToken);
        await engine.AcceptAsync(reports, DocumentBody.Parse("""{"title":"Typo","severity":"low","filed":"2026-09-20T10:00:00Z"}"""), cancellationToken: TestContext.Current.CancellationToken);
        await engine.AcceptAsync(reports, DocumentBody.Parse("""{"title":"Save slow","severity":"high","filed":"2026-09-21T10:00:00Z"}"""), cancellationToken: TestContext.Current.CancellationToken);
        await hints.DeclareAsync(new FormTypeHints(reports, "bug_reports",
        [
            new FieldHint("title", ColumnType.Text, Nullable: false),
            new FieldHint("severity", ColumnType.Text),
            new FieldHint("filed", ColumnType.Timestamp),
        ]), TestContext.Current.CancellationToken);
        await engine.ProjectAsync(reports, TestContext.Current.CancellationToken);

        var recent = new FieldFilter("filed", FilterOperator.GreaterThanOrEqual, "2026-09-14T00:00:00Z");

        var rows = await engine.QueryAsync(reports, new QuerySpec(Filters: [recent, new FieldFilter("title", FilterOperator.Contains, "SAVE")]), TestContext.Current.CancellationToken);
        rows.Rows.Should().ContainSingle().Which["title"].Should().Be("Save slow");

        var bySeverity = await engine.AggregateAsync(reports, new AggregateSpec(GroupBy: ["severity"], Filters: [recent]), TestContext.Current.CancellationToken);
        bySeverity.Stale.Should().BeFalse();
        bySeverity.Groups.Select(g => (g.Key["severity"], g.Count)).Should().Equal(("high", 1L), ("low", 1L));
    }

    [Fact]
    public async Task A_restarted_single_file_engine_keeps_its_documents_projection_and_watermarks()
    {
        var reports = FormTypeRef.Create("bug-report");
        Watermark firstHead;

        await using (var first = BuildSingleFileEngine())
        {
            var engine = first.GetRequiredService<FormbaseEngine>();
            await first.GetRequiredService<SqliteFieldHintSource>().DeclareAsync(new FormTypeHints(reports, "bug_reports",
            [
                new FieldHint("title", ColumnType.Text, Nullable: false),
                new FieldHint("closed", ColumnType.Timestamp),
            ]), TestContext.Current.CancellationToken);
            await engine.AcceptAsync(reports, DocumentBody.Parse("""{"title":"Crash on save","closed":"2026-09-10T00:00:00Z"}"""), cancellationToken: TestContext.Current.CancellationToken);
            await engine.AcceptAsync(reports, DocumentBody.Parse("""{"title":"Typo"}"""), cancellationToken: TestContext.Current.CancellationToken);
            await engine.ProjectAsync(reports, TestContext.Current.CancellationToken);
            firstHead = await first.GetRequiredService<IRawStore>().HeadAsync(reports, TestContext.Current.CancellationToken);
        }

        SqliteConnection.ClearAllPools();

        await using var second = BuildSingleFileEngine();
        var restarted = second.GetRequiredService<FormbaseEngine>();

        // No re-append, no re-projection: the file already holds both, and the stamp still names raw's head.
        var open = await restarted.QueryAsync(reports, new QuerySpec(Filters: [FieldFilter.IsNull("closed")]), TestContext.Current.CancellationToken);
        open.Stale.Should().BeFalse();
        open.Rows.Should().ContainSingle().Which["title"].Should().Be("Typo");

        var appended = await second.GetRequiredService<IRawStore>().AppendAsync(
            reports, DocumentId.New(), DocumentBody.Parse("""{"title":"Save slow"}"""), cancellationToken: TestContext.Current.CancellationToken);
        appended.Watermark.Should().BeGreaterThan(firstHead, "raw positions continue across the restart instead of starting over");

        (await restarted.QueryAsync(reports, QuerySpec.All, TestContext.Current.CancellationToken)).Stale.Should().BeTrue();
    }

    [Fact]
    public void Raw_store_and_projection_over_different_files_are_refused()
    {
        var services = new ServiceCollection();
        services.AddSqliteRawStore($"Data Source={_path}");

        var act = () => services.AddSqliteProjection($"Data Source={_path}.other");

        act.Should().Throw<ArgumentException>().WithMessage("*one file*");
    }

    private ServiceProvider BuildSingleFileEngine()
    {
        var services = new ServiceCollection();
        services.AddFormbaseCore();
        services.AddSqliteRawStore($"Data Source={_path}");
        services.AddSqliteProjection($"Data Source={_path}");
        return services.BuildServiceProvider();
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Left to the OS temp cleanup.
            }
        }
    }
}
