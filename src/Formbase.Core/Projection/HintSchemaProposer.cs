using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// First-cycle <see cref="ISchemaProposer"/>: proposes a table schema from a form type's declared
/// field hints. Returns null when no hints are declared, which makes projection a no-op. A later
/// LLM-based proposer plugs into the same port to infer schema from raw documents instead.
/// </summary>
public sealed class HintSchemaProposer : ISchemaProposer
{
    private readonly IFieldHintSource _hintSource;

    public HintSchemaProposer(IFieldHintSource hintSource) => _hintSource = hintSource;

    public async Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var hints = await _hintSource.GetHintsAsync(type, cancellationToken).ConfigureAwait(false);
        if (hints is null || hints.Fields.Count == 0)
        {
            return null;
        }

        var columns = hints.Fields
            .Select(f => new ColumnDef(f.Name, f.Type, f.Nullable))
            .ToList();

        return new TableSchema(hints.TableName, columns);
    }
}
