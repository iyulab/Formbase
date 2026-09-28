using Formbase.Core.Ports;
using Formbase.Core.Schema;
using Formbase.Sqlite;
using Microsoft.Data.Sqlite;

namespace Formbase.Core.Tests.Contracts.Sqlite;

/// <summary>A SQLite file of its own per test, removed afterwards. Needs nothing but the file system.</summary>
internal sealed class TemporarySqliteFile : IDisposable
{
    private readonly string _path;

    public TemporarySqliteFile()
    {
        _path = Path.Combine(Path.GetTempPath(), $"formbase-{Guid.NewGuid():N}.db");
        Database = new SqliteDatabase($"Data Source={_path}");
    }

    public SqliteDatabase Database { get; }

    public void Dispose()
    {
        Database.Dispose();
        // The pool keeps connections — and so the file — open until cleared.
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

/// <summary>Runs the raw-store contract against the SQLite adapter.</summary>
public sealed class SqliteRawStoreContractTests : RawStoreContractTests, IDisposable
{
    private readonly TemporarySqliteFile _file = new();

    protected override IRawStore CreateStore() => new SqliteRawStore(_file.Database);

    public void Dispose() => _file.Dispose();
}

/// <summary>Runs the projection-store contract against the SQLite adapter.</summary>
public sealed class SqliteProjectionStoreContractTests : ProjectionStoreContractTests, IDisposable
{
    private readonly TemporarySqliteFile _file = new();

    protected override IProjectionStore CreateStore() => new SqliteProjectionStore(_file.Database);

    public void Dispose() => _file.Dispose();
}

/// <summary>Runs the projection-state contract against the SQLite adapter.</summary>
public sealed class SqliteProjectionStateContractTests : ProjectionStateContractTests, IDisposable
{
    private readonly TemporarySqliteFile _file = new();

    protected override IProjectionState CreateState() => new SqliteProjectionState(_file.Database);

    public void Dispose() => _file.Dispose();
}

/// <summary>Runs the field-hint contract against the SQLite adapter.</summary>
public sealed class SqliteFieldHintSourceContractTests : FieldHintSourceContractTests, IDisposable
{
    private readonly TemporarySqliteFile _file = new();

    protected override IFieldHintSource CreateSource() => new SqliteFieldHintSource(_file.Database);

    protected override Task DeclareAsync(IFieldHintSource source, FormTypeHints hints) =>
        ((SqliteFieldHintSource)source).DeclareAsync(hints, TestContext.Current.CancellationToken);

    public void Dispose() => _file.Dispose();
}
