using Formbase.Core.Primitives;

namespace Formbase.Core.Projection;

/// <summary>
/// Folds a raw stream into its records: what a reader that wants the current state — not the history —
/// sees. A document without a key is a record of its own. Of the documents sharing a key, only the one
/// with the latest watermark stands, and if that one is a retirement the record is gone. A key retired
/// and then appended again is a record again: the fold looks only at each key's latest document.
/// </summary>
/// <remarks>
/// The projector folds this way before mapping, so a superseded document neither becomes a row nor
/// counts as a skip. It is public because every reader of the raw stream that wants records rather than
/// appends needs the same step, and writing it twice is how two readers come to disagree.
/// </remarks>
public static class RecordFold
{
    /// <summary>
    /// The documents that stand as records, in watermark order. Order of <paramref name="documents"/>
    /// does not matter; the latest watermark per key wins wherever it appears.
    /// </summary>
    public static IReadOnlyList<StoredDocument> Latest(IEnumerable<StoredDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        var standing = new List<StoredDocument>();
        var latestByKey = new Dictionary<RecordKey, StoredDocument>();
        foreach (var document in documents)
        {
            if (document.Key is not { } key)
            {
                if (!document.IsRetirement)
                {
                    standing.Add(document);
                }

                continue;
            }

            if (!latestByKey.TryGetValue(key, out var current) || document.Watermark > current.Watermark)
            {
                latestByKey[key] = document;
            }
        }

        standing.AddRange(latestByKey.Values.Where(d => !d.IsRetirement));
        standing.Sort((a, b) => a.Watermark.Value.CompareTo(b.Watermark.Value));
        return standing;
    }
}
