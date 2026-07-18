using Formbase.Core.Query;
using Formbase.Core.Schema;

namespace Formbase.Core.Ports;

/// <summary>
/// The typed-table target of a projection — the adapter seam over the backing database (MorphDB).
/// The core drives it through this port only; a real MorphDB adapter and an in-memory fake are
/// interchangeable, keeping the core free of any database dependency.
/// </summary>
public interface IProjectionStore
{
    Task<bool> TableExistsAsync(string tableName, CancellationToken cancellationToken = default);

    /// <summary>Drops a table if it exists; a no-op otherwise (idempotent).</summary>
    Task DropTableAsync(string tableName, CancellationToken cancellationToken = default);

    /// <summary>Creates a table from the schema. Fails if the table already exists (drop first).</summary>
    Task CreateTableAsync(TableSchema schema, CancellationToken cancellationToken = default);

    /// <summary>Inserts rows into an existing table; returns the number inserted.</summary>
    Task<int> BulkInsertAsync(string tableName, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken cancellationToken = default);

    /// <summary>Queries rows from a projected table. Rows carry both system and domain columns.</summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default);
}
