using System.Security.Cryptography;
using System.Text;

namespace Formbase.Core.Schema;

/// <summary>
/// A proposed table shape for projecting a form type's raw documents into a queryable table.
/// Produced by an <see cref="Ports.ISchemaProposer"/>; consumed by the projector.
/// </summary>
public sealed record TableSchema(string TableName, IReadOnlyList<ColumnDef> Columns)
{
    /// <summary>
    /// The identity of this shape — table name plus every column's name, type and nullability, in
    /// declared order — as a stable SHA-256 hex string. Two proposals fingerprint equal exactly when
    /// they would materialize the same table. Recorded in <see cref="Projection.ProjectionStamp"/>
    /// so staleness evaluation can tell a redeclared shape from a current one.
    /// </summary>
    public string Fingerprint()
    {
        var canonical = new StringBuilder(TableName);
        foreach (var column in Columns)
        {
            canonical.Append('\n').Append(column.Name)
                .Append('\u001f').Append(column.Type)
                .Append('\u001f').Append(column.Nullable);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
