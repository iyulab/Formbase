using Formbase.Core.Ports;
using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// Two proposers under one rule: what the consumer declared is carried through unchanged, and
/// inference only answers for what was never declared. The two are asked separately and neither is
/// modified — this composes them, it does not teach either one about the other.
/// <para>
/// The rule is not a preference between implementations. A declaration states things values cannot
/// show: which raw key a column reads, whether a bound value is true-then or true-now, what the
/// column points at. A proposer that observes documents can only ever name what it finds in them,
/// so letting it answer for a declared field is not a second opinion — it is the loss of a fact
/// nobody else holds. On conflict the declaration wins for that reason.
/// </para>
/// </summary>
public sealed class DeclaredFirstSchemaProposer : ISchemaProposer
{
    private readonly ISchemaProposer _declared;
    private readonly ISchemaProposer _inferred;

    /// <param name="declared">Reads what the consumer stated — typically <see cref="HintSchemaProposer"/>.</param>
    /// <param name="inferred">Observes raw documents; answers only for the undeclared.</param>
    public DeclaredFirstSchemaProposer(ISchemaProposer declared, ISchemaProposer inferred)
    {
        ArgumentNullException.ThrowIfNull(declared);
        ArgumentNullException.ThrowIfNull(inferred);
        _declared = declared;
        _inferred = inferred;
    }

    public async Task<TableSchema?> ProposeAsync(FormTypeRef type, CancellationToken cancellationToken = default)
    {
        var declared = await _declared.ProposeAsync(type, cancellationToken).ConfigureAwait(false);
        if (declared is null)
        {
            // Nothing declared for this form type — inference has the whole of it, which is the
            // case schema intelligence exists for.
            return await _inferred.ProposeAsync(type, cancellationToken).ConfigureAwait(false);
        }

        var inferred = await _inferred.ProposeAsync(type, cancellationToken).ConfigureAwait(false);
        if (inferred is null)
        {
            return declared;
        }

        // A declared column claims both the raw key it reads and the name it lands under. An
        // inferred column touching either is describing something already stated — under a
        // different name, perhaps, which is exactly what makes it invisible to a name-only check.
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var column in declared.Columns)
        {
            claimed.Add(column.ExtractionKey);
            claimed.Add(column.Name);
        }

        var columns = new List<ColumnDef>(declared.Columns);
        columns.AddRange(inferred.Columns.Where(c => !claimed.Contains(c.ExtractionKey) && !claimed.Contains(c.Name)));

        // Relations follow the same rule rather than a separate one — a declared link stands, an
        // inferred link the declaration never named is kept. Dropping the latter would be the very
        // silence this composition exists to remove, merely pointed the other way.
        var relations = declared.Relations;
        if (inferred.Relations is { Count: > 0 })
        {
            var declaredNames = new HashSet<string>(
                (relations ?? []).Select(r => r.Name), StringComparer.Ordinal);
            relations = [.. relations ?? [], .. inferred.Relations.Where(r => !declaredNames.Contains(r.Name))];
        }

        // Table name and declaration version stay the declaration's: they are the shape's identity,
        // and the observing proposer derives them from a convention rather than knowing them.
        return declared with { Columns = columns, Relations = relations };
    }
}
