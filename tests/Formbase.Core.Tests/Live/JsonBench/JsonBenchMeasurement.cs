using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Core.Query;
using Formbase.Core.Schema;
using Formbase.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Formbase.Core.Tests.Live.JsonBench;

/// <summary>
/// Borrows JSONBench's Bluesky events and its queries to time Formbase against PostgreSQL's JSONB on the
/// same machine. It is not a JSONBench entry: JSONBench forbids loading JSON into separate non-JSON columns,
/// and Formbase's projection is exactly that — declared columns over a raw store that keeps the document.
/// <para>
/// One event file (newline-delimited JSON, gzip) is loaded twice: into Formbase over SQLite (raw append,
/// then projection to declared columns, the nested keys read through JSON Pointer source keys), and into a
/// PostgreSQL <c>jsonb</c> table with JSONBench's DDL and index. Then each query Formbase can state runs three
/// times on both, and the answers are compared. Q3 (an hour derived from a timestamp) and Q5 (a span between
/// two measures) have no Formbase form yet and are reported as such, not timed.
/// </para>
/// <para>
/// <c>FORMBASE_JSONBENCH_FILE</c> names the event file, <c>FORMBASE_JSONBENCH_OUT</c> the directory for the
/// SQLite file and the report, <c>FORMBASE_JSONBENCH_LIMIT</c> how many events to load (default: all).
/// Needs Docker for PostgreSQL.
/// </para>
/// </summary>
public sealed class JsonBenchMeasurement
{
    private const int Runs = 3;
    private const int BatchSize = 10_000;

    private static readonly FormTypeRef Events = FormTypeRef.Create("bluesky");

    [Fact]
    public async Task Measure_jsonbench_queries_against_postgres_jsonb()
    {
        var file = Environment.GetEnvironmentVariable("FORMBASE_JSONBENCH_FILE");
        var outDirectory = Environment.GetEnvironmentVariable("FORMBASE_JSONBENCH_OUT");
        if (string.IsNullOrEmpty(file) || string.IsNullOrEmpty(outDirectory))
        {
            Assert.Skip("FORMBASE_JSONBENCH_FILE and FORMBASE_JSONBENCH_OUT are not set — the event data lives outside the repository.");
        }

        var limit = int.TryParse(Environment.GetEnvironmentVariable("FORMBASE_JSONBENCH_LIMIT"), out var most) && most > 0 ? most : int.MaxValue;
        var cancellation = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(outDirectory);
        var lines = ReadLines(file, limit);
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"# JSONBench queries — Formbase (SQLite) and PostgreSQL JSONB, {lines.Count:N0} events");
        report.AppendLine();
        report.AppendLine(CultureInfo.InvariantCulture, $"Event file `{Path.GetFileName(file)}` · {Runs} runs per query · times in ms (min / median of runs).");
        report.AppendLine();

        // --- Formbase over SQLite ---
        var dbPath = Path.Combine(outDirectory, "formbase.db");
        foreach (var stale in new[] { dbPath, dbPath + "-wal", dbPath + "-shm" })
        {
            File.Delete(stale);
        }

        var connection = $"Data Source={dbPath}";
        var services = new ServiceCollection().AddFormbaseCore().AddSqliteRawStore(connection).AddSqliteProjection(connection);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        await provider.GetRequiredService<SqliteFieldHintSource>().DeclareAsync(
            new FormTypeHints(Events, "bluesky",
            [
                new FieldHint("did", ColumnType.Text),
                new FieldHint("time_us", ColumnType.Integer),
                new FieldHint("kind", ColumnType.Text),
                new FieldHint("collection", ColumnType.Text, SourceKey: "/commit/collection"),
                new FieldHint("operation", ColumnType.Text, SourceKey: "/commit/operation"),
            ]),
            cancellation);

        var append = Stopwatch.StartNew();
        for (var i = 0; i < lines.Count; i += BatchSize)
        {
            var batch = lines.Skip(i).Take(BatchSize).Select(l => IntakeDocument.Accept(DocumentBody.Parse(l))).ToList();
            await engine.AcceptManyAsync(Events, batch, cancellation);
        }

        append.Stop();
        var project = Stopwatch.StartNew();
        ProjectionStatus status;
        do
        {
            await engine.ProjectAsync(Events, cancellation);
            status = await engine.GetProjectionStatusAsync(Events, cancellation);
        }
        while (status.State != ProjectionState.Projected);

        project.Stop();
        var skips = await engine.GetProjectionFieldSkipsAsync(Events, cancellation);
        report.AppendLine(CultureInfo.InvariantCulture, $"- Formbase load: raw append {append.ElapsedMilliseconds:N0} ms ({BatchSize:N0} per batch) · projection {project.ElapsedMilliseconds:N0} ms · field skips {skips.Count} · SQLite file {new FileInfo(dbPath).Length / 1024 / 1024:N0} MiB");

        // --- PostgreSQL JSONB (JSONBench's DDL, same machine) ---
        await using var postgres = new PostgreSqlBuilder("postgres:16-alpine").Build();
        await postgres.StartAsync(cancellation);
        await using var dataSource = NpgsqlDataSource.Create(postgres.GetConnectionString());
        await Execute(dataSource, """
            CREATE TABLE bluesky (data JSONB COMPRESSION lz4 NOT NULL);
            """, cancellation);
        var load = Stopwatch.StartNew();
        await using (var conn = await dataSource.OpenConnectionAsync(cancellation))
        await using (var writer = await conn.BeginTextImportAsync("COPY bluesky (data) FROM STDIN", cancellation))
        {
            foreach (var line in lines)
            {
                await writer.WriteLineAsync(EscapeCopy(line));
            }
        }

        await Execute(dataSource, """
            CREATE INDEX idx_bluesky ON bluesky ((data ->> 'kind'), (data -> 'commit' ->> 'operation'), (data -> 'commit' ->> 'collection'), (data ->> 'did'), (TO_TIMESTAMP((data ->> 'time_us')::BIGINT / 1000000.0)));
            """, cancellation);
        await Execute(dataSource, "VACUUM ANALYZE bluesky", cancellation);
        load.Stop();
        report.AppendLine(CultureInfo.InvariantCulture, $"- PostgreSQL load: COPY + index + VACUUM ANALYZE {load.ElapsedMilliseconds:N0} ms (postgres:16-alpine, default settings)");
        report.AppendLine();
        report.AppendLine("| Query | Formbase (SQLite) | PostgreSQL JSONB | Formbase / PostgreSQL | Same answer |");
        report.AppendLine("|---|---|---|---|---|");

        var created = new[] { FieldFilter.Equal("kind", "commit"), FieldFilter.Equal("operation", "create") };
        var queries = new (string Name, AggregateSpec Spec, string Sql)[]
        {
            ("Q1 events per collection",
                new AggregateSpec(GroupBy: ["collection"], OrderBy: [GroupOrder.ByCount(descending: true)]),
                "SELECT data -> 'commit' ->> 'collection' AS event, COUNT(*) AS count FROM bluesky GROUP BY event ORDER BY count DESC"),
            ("Q2 created events and distinct users per collection",
                new AggregateSpec(GroupBy: ["collection"], Filters: created, Measures: [AggregateMeasure.CountDistinct("did")], OrderBy: [GroupOrder.ByCount(descending: true)]),
                "SELECT data -> 'commit' ->> 'collection' AS event, COUNT(*) AS count, COUNT(DISTINCT data ->> 'did') AS users FROM bluesky WHERE data ->> 'kind' = 'commit' AND data -> 'commit' ->> 'operation' = 'create' GROUP BY event ORDER BY count DESC"),
            ("Q4 first three posters",
                new AggregateSpec(GroupBy: ["did"], Filters: [.. created, FieldFilter.Equal("collection", "app.bsky.feed.post")], Measures: [AggregateMeasure.Min("time_us")], OrderBy: [GroupOrder.ByMeasure(0)], Limit: 3),
                "SELECT data ->> 'did' AS user_id, MIN((data ->> 'time_us')::BIGINT) AS first_post FROM bluesky WHERE data ->> 'kind' = 'commit' AND data -> 'commit' ->> 'operation' = 'create' AND data -> 'commit' ->> 'collection' = 'app.bsky.feed.post' GROUP BY user_id ORDER BY first_post ASC LIMIT 3"),
        };

        foreach (var (name, spec, sql) in queries)
        {
            var formbaseTimes = new List<double>();
            AggregateResult? answer = null;
            for (var run = 0; run < Runs; run++)
            {
                var clock = Stopwatch.StartNew();
                answer = await engine.AggregateAsync(Events, spec, cancellation);
                formbaseTimes.Add(clock.Elapsed.TotalMilliseconds);
            }

            var postgresTimes = new List<double>();
            List<object?[]> rows = [];
            for (var run = 0; run < Runs; run++)
            {
                var clock = Stopwatch.StartNew();
                rows = await Query(dataSource, sql, cancellation);
                postgresTimes.Add(clock.Elapsed.TotalMilliseconds);
            }

            var same = Same(answer!, rows, spec);
            report.AppendLine(CultureInfo.InvariantCulture,
                $"| {name} | {Format(formbaseTimes)} | {Format(postgresTimes)} | {Median(formbaseTimes) / Median(postgresTimes):F2}× | {(same ? "yes" : "**no**")} |");
        }

        report.AppendLine("| Q3 events per hour of day | — no form yet: an hour derived from `time_us` needs a computed column | | | |");
        report.AppendLine("| Q5 longest posting span | — no form: a span is a difference of two measures | | | |");
        await File.WriteAllTextAsync(Path.Combine(outDirectory, "report.md"), report.ToString(), cancellation);
        TestContext.Current.SendDiagnosticMessage(report.ToString());
    }

    private static List<string> ReadLines(string file, int limit)
    {
        using var stream = File.OpenRead(file);
        using var gzip = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var lines = new List<string>();
        while (lines.Count < limit && reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    // COPY text format: backslash, newline, carriage return and tab are escaped.
    private static string EscapeCopy(string line) =>
        line.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal);

    private static async Task Execute(NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.CommandTimeout = 0;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<List<object?[]>> Query(NpgsqlDataSource dataSource, string sql, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(sql);
        command.CommandTimeout = 0;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var rows = new List<object?[]>();
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++)
            {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            rows.Add(row);
        }

        return rows;
    }

    /// <summary>
    /// Compares the two answers as sets of (group key, count, measures): ties in the order are broken
    /// differently by the two engines, so order is not compared — only what each group holds, and for a
    /// limited ranking, that the measures ranked are the same.
    /// </summary>
    private static bool Same(AggregateResult formbase, List<object?[]> postgres, AggregateSpec spec)
    {
        if (formbase.Groups.Count != postgres.Count)
        {
            return false;
        }

        if (spec.Limit is not null)
        {
            var ours = formbase.Groups.Select(g => Convert.ToInt64(g.Measures![0].Value, CultureInfo.InvariantCulture)).Order().ToList();
            var theirs = postgres.Select(r => Convert.ToInt64(r[1], CultureInfo.InvariantCulture)).Order().ToList();
            return ours.SequenceEqual(theirs);
        }

        var expected = postgres.Select(r => string.Join('|', r.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)))).Order(StringComparer.Ordinal);
        var actual = formbase.Groups.Select(g => string.Join('|',
            new[] { Convert.ToString(g.Key[spec.GroupBy![0]], CultureInfo.InvariantCulture), g.Count.ToString(CultureInfo.InvariantCulture) }
                .Concat((g.Measures ?? []).Select(m => Convert.ToString(m.Value, CultureInfo.InvariantCulture))))).Order(StringComparer.Ordinal);
        return expected.SequenceEqual(actual, StringComparer.Ordinal);
    }

    private static double Median(List<double> times) => times.Order().ElementAt(times.Count / 2);

    private static string Format(List<double> times) =>
        string.Create(CultureInfo.InvariantCulture, $"{times.Min():F0} / {Median(times):F0}");
}
