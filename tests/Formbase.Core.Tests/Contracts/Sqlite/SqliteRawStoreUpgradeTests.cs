using Formbase.Core.Primitives;
using Formbase.Sqlite;
using Microsoft.Data.Sqlite;

namespace Formbase.Core.Tests.Contracts.Sqlite;

/// <summary>
/// A file written before record keys existed. <c>CREATE TABLE IF NOT EXISTS</c> leaves its raw table as
/// it was, so without an upgrade the first keyed append fails on a missing column — these pin that the
/// store brings the file forward and that what it already held reads back unchanged.
/// </summary>
public sealed class SqliteRawStoreUpgradeTests : IDisposable
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly Guid OldId = Guid.NewGuid();

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"formbase-upgrade-{Guid.NewGuid():N}.db");

    public SqliteRawStoreUpgradeTests()
    {
        // The raw table exactly as the earlier version created it, holding one document.
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            CREATE TABLE fb_raw_documents (
                watermark   INTEGER PRIMARY KEY AUTOINCREMENT,
                id          TEXT    NOT NULL UNIQUE,
                form_type   TEXT    NOT NULL,
                body        TEXT    NOT NULL,
                appended_at TEXT    NOT NULL
            );
            INSERT INTO fb_raw_documents (id, form_type, body, appended_at)
            VALUES ('{{OldId:D}}', 'qc', '{"n":1}', '2026-01-01T00:00:00.0000000Z');
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_document_from_before_the_upgrade_reads_back_as_a_record_of_its_own()
    {
        using var database = new SqliteDatabase($"Data Source={_path}");
        var store = new SqliteRawStore(database);

        var old = await store.GetAsync(DocumentId.From(OldId), TestContext.Current.CancellationToken);

        old.Should().NotBeNull();
        old!.Key.Should().BeNull();
        old.IsRetirement.Should().BeFalse();
        old.Body!.Root.GetProperty("n").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task An_upgraded_file_takes_keyed_appends_and_retirements()
    {
        using var database = new SqliteDatabase($"Data Source={_path}");
        var store = new SqliteRawStore(database);
        var key = RecordKey.Create("a");

        var keyed = await store.AppendAsync(Qc, DocumentId.New(), DocumentBody.Parse("""{"n":2}"""), key, TestContext.Current.CancellationToken);
        var retirement = await store.RetireAsync(Qc, DocumentId.New(), key, TestContext.Current.CancellationToken);

        keyed.Watermark.Should().Be(new Watermark(2), "positions continue after the documents the file already held");
        (await store.GetAsync(retirement.Id, TestContext.Current.CancellationToken))!.IsRetirement.Should().BeTrue();
    }

    [Fact]
    public async Task A_document_from_before_the_upgrade_has_no_known_declaration_and_a_new_one_keeps_its_stamp()
    {
        using var database = new SqliteDatabase($"Data Source={_path}");
        var store = new SqliteRawStore(database);
        var ct = TestContext.Current.CancellationToken;

        var stamped = await store.AppendAsync(Qc, RawAppend.Document(DocumentId.New(), DocumentBody.Parse("""{"n":2}"""), declarationSeq: 4), ct);

        (await store.GetAsync(DocumentId.From(OldId), ct))!.DeclarationSeq.Should().BeNull();
        (await store.GetAsync(stamped.Id, ct))!.DeclarationSeq.Should().Be(4);
    }

    /// <summary>The shape the last published version wrote — keys and retirements, no declaration sequence.</summary>
    [Fact]
    public async Task A_file_from_the_last_published_version_gains_the_declaration_column_and_keeps_what_it_held()
    {
        var path = Path.Combine(Path.GetTempPath(), $"formbase-upgrade-{Guid.NewGuid():N}.db");
        var held = Guid.NewGuid();
        try
        {
            await using (var connection = new SqliteConnection($"Data Source={path}"))
            {
                await connection.OpenAsync(TestContext.Current.CancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText =
                    $$"""
                    CREATE TABLE fb_raw_documents (
                        watermark   INTEGER PRIMARY KEY AUTOINCREMENT,
                        id          TEXT    NOT NULL UNIQUE,
                        form_type   TEXT    NOT NULL,
                        body        TEXT    NOT NULL,
                        appended_at TEXT    NOT NULL,
                        record_key  TEXT    NULL,
                        retired     INTEGER NOT NULL DEFAULT 0
                    );
                    INSERT INTO fb_raw_documents (id, form_type, body, appended_at, record_key, retired)
                    VALUES ('{{held:D}}', 'qc', '{"n":1}', '2026-01-01T00:00:00.0000000Z', 'k', 0);
                    """;
                await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
            }

            using var database = new SqliteDatabase($"Data Source={path}");
            var store = new SqliteRawStore(database);
            var ct = TestContext.Current.CancellationToken;

            var old = await store.GetAsync(DocumentId.From(held), ct);
            var next = await store.AppendAsync(Qc, RawAppend.Document(DocumentId.New(), DocumentBody.Parse("""{"n":2}"""), RecordKey.Create("k"), declarationSeq: 0), ct);

            old!.Key.Should().Be(RecordKey.Create("k"));
            old.DeclarationSeq.Should().BeNull();
            (await store.GetAsync(next.Id, ct))!.DeclarationSeq.Should().Be(0);
            next.Watermark.Should().Be(new Watermark(2));
        }
        finally
        {
            TemporarySqliteFile.ClearPool($"Data Source={path}");
            foreach (var file in new[] { path, path + "-wal", path + "-shm" })
            {
                try
                {
                    File.Delete(file);
                }
                catch (IOException)
                {
                    // Another test's pool may still hold it; the temp directory is the OS's to clean.
                }
            }
        }
    }

    [Fact]
    public async Task A_second_process_opening_the_upgraded_file_finds_nothing_left_to_do()
    {
        using (var first = new SqliteDatabase($"Data Source={_path}"))
        {
            await new SqliteRawStore(first).HeadAsync(Qc, TestContext.Current.CancellationToken);
        }

        using var second = new SqliteDatabase($"Data Source={_path}");
        var head = await new SqliteRawStore(second).HeadAsync(Qc, TestContext.Current.CancellationToken);

        head.Should().Be(new Watermark(1));
    }

    public void Dispose()
    {
        TemporarySqliteFile.ClearPool($"Data Source={_path}");
        foreach (var path in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // Another test's pool may still hold it; the temp directory is the OS's to clean.
            }
        }
    }
}
