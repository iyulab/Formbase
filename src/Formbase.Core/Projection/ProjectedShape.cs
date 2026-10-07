using System.Security.Cryptography;
using System.Text;
using Formbase.Core.Schema;

namespace Formbase.Core.Projection;

/// <summary>
/// The shape a projection materializes from a declared schema: the declaration's own shape plus the
/// system columns every projected row carries. Its fingerprint is what a
/// <see cref="ProjectionStamp"/> records and what staleness is judged by, so a change to the system
/// columns reads as a shape change everywhere a redeclaration already does — a projection built
/// before it is stale, rebuilt rather than brought forward, and fired as shape drift — without a
/// second axis to keep in step.
/// </summary>
public static class ProjectedShape
{
    // Unit separator between fields of a record; newline between records.
    private const char Sep = '\u001f';

    /// <summary>The fingerprint of the table <paramref name="schema"/> is projected into.</summary>
    public static string Fingerprint(TableSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        var canonical = new StringBuilder(schema.Fingerprint());
        foreach (var column in ProjectionSystemColumns.All)
        {
            canonical.Append('\n').Append(column.Name)
                .Append(Sep).Append(column.Type)
                .Append(Sep).Append(column.Nullable);
        }

        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString())));
    }
}
