using Formbase.Core.Primitives;
using Formbase.Core.Projection;

namespace Formbase.Core.Ports;

/// <summary>
/// Projects a form type's raw documents into a queryable table via drop-and-rebuild. Because the
/// raw store is the source of truth, a shape change needs no ALTER diffing — the table is rebuilt.
/// A projection with no proposed schema is a no-op.
/// </summary>
public interface IProjector
{
    Task<ProjectionResult> ProjectAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}
