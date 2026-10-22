using System.Security.Cryptography;
using System.Text;

namespace Formbase.Core.Schema;

/// <summary>
/// A proposed table shape for projecting a form type's raw documents into a queryable table.
/// Produced by an <see cref="Ports.ISchemaProposer"/>; consumed by the projector.
/// <paramref name="Relations"/> carries declared links to other tables to the projection store
/// (stage-1: delivered, materialized by stores that can), and <paramref name="DeclarationVersion"/>
/// travels with the shape so its identity moves when the declaration does. <paramref name="Section"/>
/// is set when the table is a repeated section: its rows are cut out of another form type's documents.
/// <paramref name="Indexes"/> are the declared indexes the store builds with the table.
/// </summary>
public sealed record TableSchema(
    string TableName,
    IReadOnlyList<ColumnDef> Columns,
    IReadOnlyList<RelationDef>? Relations = null,
    int DeclarationVersion = 1,
    SectionDef? Section = null,
    IReadOnlyList<IndexDef>? Indexes = null)
{
    // Unit separator between fields of a record; newline between records.
    private const char Sep = '';

    /// <summary>
    /// The identity of this shape — table name, declaration version, every column's full definition
    /// (name, type, nullability, source key, binding, binding target) in declared order, every declared
    /// relation, and the section the rows are cut from — as a stable SHA-256 hex string. Two proposals
    /// fingerprint equal exactly when they would materialize the same table. Recorded in
    /// <see cref="Projection.ProjectionStamp"/> so staleness evaluation can tell a redeclared shape
    /// from a current one; a change on any declaration axis therefore reads as a new shape.
    /// </summary>
    public string Fingerprint()
    {
        var canonical = new StringBuilder(TableName)
            .Append(Sep).Append(DeclarationVersion);
        foreach (var column in Columns)
        {
            canonical.Append('\n').Append(column.Name)
                .Append(Sep).Append(column.Type)
                .Append(Sep).Append(column.Nullable)
                .Append(Sep).Append(column.SourceKey ?? string.Empty)
                .Append(Sep).Append(column.Binding)
                .Append(Sep).Append(column.BindingTarget ?? string.Empty);
            if (column.Derivation is { } derivation)
            {
                // Only when present, so a schema without computed fields keeps its fingerprint.
                canonical.Append(Sep).Append('ƒ').Append(derivation.Canonical());
            }

            if (column.Reference is { } reference)
            {
                // Appended only when present, so a schema without resolved references keeps the
                // fingerprint it had before references were resolved.
                canonical.Append(Sep).Append(reference.TargetTable)
                    .Append(Sep).Append(reference.ValueColumn)
                    .Append(Sep).Append(reference.Lookup)
                    .Append(Sep).Append(reference.ViaColumn);
                if (reference.TargetIsSection)
                {
                    // Only when set: a reference into a table that is not a section keeps its fingerprint.
                    canonical.Append(Sep).Append('§');
                }
            }
        }

        foreach (var relation in Relations ?? [])
        {
            canonical.Append('\n').Append('~').Append(relation.Name)
                .Append(Sep).Append(relation.Kind)
                .Append(Sep).Append(relation.TargetTable)
                .Append(Sep).Append(relation.KeyColumn);
        }

        if (Section is { } section)
        {
            // Appended only when present, so a table that is not a section keeps the fingerprint it had
            // before sections existed. Where the rows come from is part of what the table holds.
            canonical.Append('\n').Append('§').Append(section.ParentType.Value)
                .Append(Sep).Append(section.SectionField)
                .Append(Sep).Append(section.ParentKey ?? string.Empty)
                .Append(Sep).Append(section.RowKey);
            if (section.ValueMember is { } valueMember)
            {
                // Only for a section of values, so a section of objects keeps its fingerprint.
                canonical.Append(Sep).Append('=').Append(valueMember);
            }
        }

        foreach (var index in Indexes ?? [])
        {
            // Only when declared, so a table without declared indexes keeps the fingerprint it had before
            // indexes could be declared. An index is built with the table, so a changed one rebuilds it.
            canonical.Append('\n').Append('#').AppendJoin(Sep, index.Columns);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
