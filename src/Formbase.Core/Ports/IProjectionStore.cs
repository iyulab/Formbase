using Formbase.Core.Primitives;
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

    /// <summary>
    /// Brings an existing table forward without rebuilding it: removes every row whose
    /// <see cref="Projection.ProjectionSystemColumns.RecordKey"/> is one of <paramref name="removeKeys"/>
    /// or whose <see cref="Projection.ProjectionSystemColumns.DocumentId"/> is one of
    /// <paramref name="removeDocuments"/>, then inserts <paramref name="rows"/>; returns the number
    /// inserted. Keys and documents with no row are ignored, so applying the same call twice leaves the
    /// table as applying it once — the property a retry after a failure between the rows and the
    /// projection state relies on. A store with transactions applies the call in one; one without
    /// relies on that idempotence instead.
    /// </summary>
    Task<int> ReplaceRowsAsync(
        string tableName,
        IReadOnlyCollection<RecordKey> removeKeys,
        IReadOnlyCollection<DocumentId> removeDocuments,
        IReadOnlyList<IReadOnlyDictionary<string, object?>> rows,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Queries rows from a projected table. Rows carry both system and domain columns: the caller reads
    /// which record each row is from <see cref="Projection.ProjectionSystemColumns.DocumentId"/> (a
    /// <see cref="Guid"/> or its text; required) and <see cref="Projection.ProjectionSystemColumns.RecordKey"/>
    /// (text, or null or absent for a document that is a record of its own). Every
    /// <see cref="FieldFilter"/> applies; the caller has already checked that each operator applies to
    /// its column's type and coerced each value to it.
    /// </summary>
    Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> QueryAsync(string tableName, QuerySpec spec, CancellationToken cancellationToken = default);

    /// <summary>
    /// Counts the rows the spec's filters keep, one group per distinct combination of the grouping
    /// columns (one group, possibly counting zero, when there are none). Group order is unspecified —
    /// the caller orders them. Filters arrive checked and coerced, as for <see cref="QueryAsync"/>.
    /// When <see cref="AggregateSpec.RecordsPerGroup"/> is set, each group carries
    /// <see cref="AggregateGroup.Records"/>: each record's document with its key, in watermark order, cut
    /// at that limit; otherwise <see cref="AggregateGroup.Records"/> is null.
    /// </summary>
    Task<IReadOnlyList<AggregateGroup>> AggregateAsync(string tableName, AggregateSpec spec, CancellationToken cancellationToken = default);
}
