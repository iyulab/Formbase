namespace Formbase.Host.Contracts;

/// <summary>
/// Records read from a form type's projection.
/// </summary>
/// <param name="Rows">
/// The projected rows, each carrying exactly the declared columns — the projection's own
/// bookkeeping is not part of what a caller reads, and an internal that leaks here would calcify
/// into their contract.
/// </param>
/// <param name="Stale">
/// True when raw documents arrived after the projection was built, so these rows are current as of
/// an earlier point in the stream. The rows are still served: a stale answer a caller knows is
/// stale is more useful than a refusal.
/// </param>
public sealed record RecordQueryResponse(
    IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows,
    bool Stale);
