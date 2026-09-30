using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;
using Formbase.Sqlite;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace Formbase.Core.Tests.Contracts.Sqlite;

/// <summary>
/// A single SQLite file keeps the raw documents, the projection state and the declarations beside the
/// projected tables, and a projection rebuild drops the table it projects into. A projection named after
/// one of the engine's own tables must therefore never be built or dropped — even when its declaration
/// was stored before declaration writers checked the name.
/// </summary>
public sealed class SqliteReservedTableTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"formbase-reserved-{Guid.NewGuid():N}.db");

    [Theory]
    [InlineData("fb_raw_documents")]
    [InlineData("fb_field_hints")]
    [InlineData("FB_PROJECTION_COLUMNS")]
    public async Task The_projection_store_refuses_to_drop_or_create_a_reserved_table(string tableName)
    {
        using var database = new SqliteDatabase($"Data Source={_path}");
        var store = new SqliteProjectionStore(database);

        var drop = () => store.DropTableAsync(tableName, TestContext.Current.CancellationToken);
        var create = () => store.CreateTableAsync(
            new TableSchema(tableName, [new ColumnDef("serial", ColumnType.Text, Nullable: true)]),
            TestContext.Current.CancellationToken);

        await drop.Should().ThrowAsync<InvalidOperationException>();
        await create.Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>
    /// The failure this exists for: before declarations were checked, a form type declared into
    /// <c>fb_raw_documents</c> replaced the raw table on its first projection and every document of every
    /// form type in the file was gone. A declaration like that may still be on disk.
    /// </summary>
    [Fact]
    public async Task A_stored_declaration_naming_the_raw_table_cannot_destroy_the_raw_documents()
    {
        var connectionString = $"Data Source={_path}";
        var services = new ServiceCollection();
        services.AddFormbaseCore();
        services.AddSqliteRawStore(connectionString);
        services.AddSqliteProjection(connectionString);
        await using var provider = services.BuildServiceProvider();
        var engine = provider.GetRequiredService<FormbaseEngine>();
        var raw = provider.GetRequiredService<IRawStore>();
        var hints = provider.GetRequiredService<SqliteFieldHintSource>();

        var ledger = FormTypeRef.Create("ledger");
        var intruder = FormTypeRef.Create("intruder");
        for (var i = 0; i < 3; i++)
        {
            await engine.AcceptAsync(ledger, DocumentBody.Parse($$"""{"n":{{i}}}"""), cancellationToken: TestContext.Current.CancellationToken);
        }

        await engine.AcceptAsync(intruder, DocumentBody.Parse("""{"serial":"x"}"""), cancellationToken: TestContext.Current.CancellationToken);

        // Stand the table up through a legal declaration, then write the reserved name the way an older
        // version would have stored it — past the writer's check.
        await hints.DeclareAsync(new FormTypeHints(intruder, "intruder_rows", [new FieldHint("serial", ColumnType.Text)]), TestContext.Current.CancellationToken);
        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(TestContext.Current.CancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE fb_field_hints SET table_name = 'fb_raw_documents' WHERE form_type = 'intruder'";
            (await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken)).Should().Be(1);
        }

        var project = () => engine.ProjectAsync(intruder, TestContext.Current.CancellationToken);

        await project.Should().ThrowAsync<Exception>();

        var left = 0;
        await foreach (var _ in raw.StreamAsync(ledger, Watermark.Zero, TestContext.Current.CancellationToken))
        {
            left++;
        }

        left.Should().Be(3, "the raw documents are the source of truth and a declaration must not be able to drop them");
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
