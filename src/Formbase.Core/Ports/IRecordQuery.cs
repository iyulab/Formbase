using Formbase.Core.Primitives;
using Formbase.Core.Query;

namespace Formbase.Core.Ports;

/// <summary>
/// The "system's question" read path — queries and aggregates over projected records. Throws
/// <see cref="Errors.NotProjectedException"/> when the form type has no projection (never an empty
/// result), and <see cref="Errors.ProjectionUnavailableException"/> when the backing store is down.
/// </summary>
public interface IRecordQuery
{
    Task<QueryResult> QueryAsync(FormTypeRef type, QuerySpec spec, CancellationToken cancellationToken = default);
}
