using System.Security.Cryptography;
using System.Text;

namespace Formbase.Core.Schema;

/// <summary>
/// A proposed table shape for projecting a form type's raw documents into a queryable table.
/// Produced by an <see cref="Ports.ISchemaProposer"/>; consumed by the projector.
/// <paramref name="Relations"/> carries declared links to other tables to the projection store
/// (stage-1: delivered, materialized by stores that can), and <paramref name="DeclarationVersion"/>
/// travels with the shape so its identity moves when the declaration does.
/// </summary>
public sealed record TableSchema(
    string TableName,
    IReadOnlyList<ColumnDef> Columns,
    IReadOnlyList<RelationDef>? Relations = null,
    int DeclarationVersion = 1)
{
    // Unit separator between fields of a record; newline between records.
    private const char Sep = '';

    /// <summary>
    /// The identity of this shape — table name, declaration version, every column's full definition
    /// (name, type, nullability, source key, binding, binding target) in declared order, and every
    /// declared relation — as a stable SHA-256 hex string. Two proposals fingerprint equal exactly
    /// when they would materialize the same table. Recorded in
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
        }

        foreach (var relation in Relations ?? [])
        {
            canonical.Append('\n').Append('~').Append(relation.Name)
                .Append(Sep).Append(relation.Kind)
                .Append(Sep).Append(relation.TargetTable)
                .Append(Sep).Append(relation.KeyColumn);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
