using Formbase.Core.Primitives;

namespace Formbase.Core.Query;

/// <summary>
/// Result of a record query against a projected table. <see cref="Stale"/> flags that the
/// projection was current-but-behind the raw head when read.
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<RecordRow> Rows,
    bool Stale);

/// <summary>
/// One record read from a projection: which record it is, and its declared fields.
/// </summary>
/// <param name="Record">
/// Which record the row is — the same identity an aggregate names as its evidence
/// (<see cref="AggregateGroup.Records"/>), so a count and the rows behind it can be matched up.
/// </param>
/// <param name="Fields">
/// Exactly the declared columns, each by its declared name. The projection's own bookkeeping is not
/// among them: the record's identity is carried by <see cref="Record"/>, and anything else the backing
/// store keeps is not the caller's to depend on.
/// </param>
public sealed record RecordRow(RecordRef Record, IReadOnlyDictionary<string, object?> Fields);
