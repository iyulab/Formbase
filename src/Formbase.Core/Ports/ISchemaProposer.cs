using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Ports;

/// <summary>
/// The seam where schema intelligence plugs in. Given a form type, proposes a table shape to
/// project into — or null when it cannot (e.g. no field hints yet), which makes projection a no-op.
/// The first-cycle implementation reads declared field hints; a later LLM-based proposer observes
/// raw documents. Both satisfy this same contract, so swapping one for the other never touches the core.
/// </summary>
public interface ISchemaProposer
{
    Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default);
}
