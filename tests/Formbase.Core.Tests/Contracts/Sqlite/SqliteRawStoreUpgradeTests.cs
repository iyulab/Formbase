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
        SqliteConnection.ClearAllPools();
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
