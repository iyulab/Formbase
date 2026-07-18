namespace Formbase.Core.Query;

/// <summary>
/// Result of a record query against a projected table. <see cref="Stale"/> flags that the
/// projection was current-but-behind the raw head when read.
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Stale);
