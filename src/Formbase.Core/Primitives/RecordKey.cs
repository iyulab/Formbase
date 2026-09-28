namespace Formbase.Core.Primitives;

/// <summary>
/// Names the record a document belongs to, so a later document can correct or retire it. Scoped to a
/// form type: the same key under two form types names two records. Opaque to the engine — compared
/// ordinally and stored as given, never trimmed, case-folded or Unicode-normalized, because only the caller
/// knows what makes two of its keys the same record — normalize keys derived from file names or user input
/// before creating them.
/// <para>
/// A document without a key is a record of its own, as every document was before keys existed. A
/// document with a key replaces the key's earlier documents in the projection: the latest watermark
/// wins, and a retirement as the latest removes the record (see <see cref="StoredDocument"/>).
/// </para>
/// </summary>
public readonly record struct RecordKey
{
    /// <summary>The key exactly as the caller gave it.</summary>
    public string Value { get; }

    private RecordKey(string value) => Value = value;

    /// <summary>Creates a record key, rejecting null or blank input.</summary>
    /// <exception cref="ArgumentException"><paramref name="value"/> is null, empty, or only whitespace.</exception>
    public static RecordKey Create(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A record key must be a non-blank string.", nameof(value));
        }

        return new RecordKey(value);
    }

    public override string ToString() => Value;
}
