using Formbase.Core.Primitives;
using Formbase.Core.Projection;
using Formbase.Sqlite;
using Microsoft.Data.Sqlite;

namespace Formbase.Core.Tests.Contracts.Sqlite;

/// <summary>
/// A file written before skips carried record keys. Its stamp must read back as not keyed and its skips
/// without keys — what tells the next projection to rebuild rather than bring the table forward on skips
/// it cannot attribute to a record — and the upgraded file must take keyed skips and deltas.
/// </summary>
public sealed class SqliteProjectionStateUpgradeTests : IDisposable
{
    private static readonly FormTypeRef Qc = FormTypeRef.Create("qc");
    private static readonly Guid OldSkipped = Guid.NewGuid();

    private readonly string _path = Path.Combine(Path.GetTempPath(), $"formbase-state-upgrade-{Guid.NewGuid():N}.db");

    public SqliteProjectionStateUpgradeTests()
    {
        // The state tables exactly as the earlier version created them, holding one run with one skip.
        using var connection = new SqliteConnection($"Data Source={_path}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText =
            $$"""
            CREATE TABLE fb_projection_state (
                form_type          TEXT    PRIMARY KEY,
                watermark          INTEGER NOT NULL,
                table_name         TEXT    NOT NULL,
                schema_fingerprint TEXT    NOT NULL,
                verified           INTEGER NOT NULL
            );
            CREATE TABLE fb_projection_skips (
                form_type   TEXT    NOT NULL,
                ordinal     INTEGER NOT NULL,
                document_id TEXT    NOT NULL,
                reason      TEXT    NOT NULL,
                PRIMARY KEY (form_type, ordinal)
            );
            CREATE TABLE fb_projection_field_skips (
                form_type   TEXT    NOT NULL,
                ordinal     INTEGER NOT NULL,
                document_id TEXT    NOT NULL,
                field       TEXT    NOT NULL,
                reason      TEXT    NOT NULL,
                PRIMARY KEY (form_type, ordinal)
            );
            INSERT INTO fb_projection_state VALUES ('qc', 5, 'qc', 'fp-1', 1);
            INSERT INTO fb_projection_skips VALUES ('qc', 0, '{{OldSkipped:D}}', 'old reason');
            """;
        command.ExecuteNonQuery();
    }

    [Fact]
    public async Task A_run_recorded_before_the_upgrade_reads_back_unkeyed()
    {
        using var database = new SqliteDatabase($"Data Source={_path}");
        var state = new SqliteProjectionState(database);

        (await state.GetAsync(Qc, TestContext.Current.CancellationToken)).Should().Be(new ProjectionStamp(new Watermark(5), "qc", "fp-1", Verified: true, SkipsKeyed: false));
        (await state.GetSkipsAsync(Qc, TestContext.Current.CancellationToken)).Should().Equal(new ProjectionSkip(DocumentId.From(OldSkipped), "old reason"));
    }

    [Fact]
    public async Task An_upgraded_file_takes_keyed_skips_and_deltas()
    {
        using var database = new SqliteDatabase($"Data Source={_path}");
        var state = new SqliteProjectionState(database);
        var keyed = new ProjectionSkip(DocumentId.New(), "keyed", RecordKey.Create("a"));
        await state.SetProjectedAsync(Qc, new ProjectionStamp(new Watermark(6), "qc", "fp-1", SkipsKeyed: true), [keyed], [], TestContext.Current.CancellationToken);

        var applied = await state.ApplyProjectedDeltaAsync(Qc, new Watermark(6), new ProjectionStamp(new Watermark(8), "qc", "fp-1", SkipsKeyed: true), [RecordKey.Create("a")], [], [], [], TestContext.Current.CancellationToken);

        applied.Should().BeTrue();
        (await state.GetSkipsAsync(Qc, TestContext.Current.CancellationToken)).Should().BeEmpty();
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
