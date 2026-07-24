using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// First-cycle <see cref="ISchemaProposer"/>: proposes a table schema from a form type's declared
/// field hints. Returns null when no hints are declared, which makes projection a no-op. A later
/// LLM-based proposer plugs into the same port to infer schema from raw documents instead.
/// Resolves each declared axis to its physical rendering: a field's <see cref="EntityRef"/> and a
/// relation's target form type become <c>table.column</c> / table names, looked up from the
/// target's own declared hints and falling back to the form-type name (the table-name convention).
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

        var columns = new List<ColumnDef>(hints.Fields.Count);
        foreach (var field in hints.Fields)
        {
            var bindingTarget = field.Target is null
                ? null
                : $"{await ResolveTableAsync(field.Target.Entity, cancellationToken).ConfigureAwait(false)}.{field.Target.KeyField}";
            columns.Add(new ColumnDef(field.Name, field.Type, field.Nullable, field.SourceKey, field.Binding, bindingTarget));
        }

        List<RelationDef>? relations = null;
        if (hints.Relations is { Count: > 0 })
        {
            relations = new List<RelationDef>(hints.Relations.Count);
            foreach (var relation in hints.Relations)
            {
                var targetTable = await ResolveTableAsync(relation.Target, cancellationToken).ConfigureAwait(false);
                relations.Add(new RelationDef(relation.Name, relation.Kind, targetTable, relation.KeyField));
            }
        }

        return new TableSchema(hints.TableName, columns, relations, hints.DeclarationVersion);
    }

    /// <summary>The target's declared table name, or its form-type name when it declares no hints yet.</summary>
    private async Task<string> ResolveTableAsync(FormTypeRef target, CancellationToken cancellationToken)
    {
        var targetHints = await _hintSource.GetHintsAsync(target, cancellationToken).ConfigureAwait(false);
        return targetHints?.TableName ?? target.Value;
    }
}
