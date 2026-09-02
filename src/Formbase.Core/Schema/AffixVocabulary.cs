using Formbase.Core.Primitives;

namespace Formbase.Core.Schema;

/// <summary>
/// Reads a modality/state tag from a <see cref="FormTypeRef"/>'s name by matching it against a
/// caller-supplied affix-to-tag vocabulary — "the class is in the form name, the action is in the
/// suffix" (docs/CONSTITUTION.md §1, "read, not inferred"). Formbase ships no built-in affix list;
/// the vocabulary is entirely caller-supplied so this works for any language or naming convention,
/// not only the Korean document-suffix conventions that motivated it.
/// </summary>
public sealed class AffixVocabulary
{
    private readonly IReadOnlyDictionary<string, string> _affixToTag;

    /// <summary>Creates a vocabulary from a non-empty affix-&gt;tag map; rejects blank affixes or tags.</summary>
    public AffixVocabulary(IReadOnlyDictionary<string, string> affixToTag)
    {
        if (affixToTag is null || affixToTag.Count == 0)
        {
            throw new ArgumentException("A vocabulary must declare at least one affix.", nameof(affixToTag));
        }

        foreach (var (affix, tag) in affixToTag)
        {
            if (string.IsNullOrWhiteSpace(affix))
            {
                throw new ArgumentException("An affix must be a non-empty string.", nameof(affixToTag));
            }

            if (string.IsNullOrWhiteSpace(tag))
            {
                throw new ArgumentException("A tag must be a non-empty string.", nameof(affixToTag));
            }
        }

        _affixToTag = affixToTag;
    }

    /// <summary>
    /// Returns the tag for the longest declared affix that <paramref name="type"/>'s name ends
    /// with, or null when none match. Longest-match-wins so a more specific affix
    /// (e.g. "-cheongguseo") outranks a shorter one it happens to contain (e.g. "-seo").
    /// </summary>
    public string? Resolve(FormTypeRef type)
    {
        string? bestTag = null;
        var bestLength = -1;

        foreach (var (affix, tag) in _affixToTag)
        {
            if (affix.Length > bestLength && type.Value.EndsWith(affix, StringComparison.Ordinal))
            {
                bestTag = tag;
                bestLength = affix.Length;
            }
        }

        return bestTag;
    }
}
