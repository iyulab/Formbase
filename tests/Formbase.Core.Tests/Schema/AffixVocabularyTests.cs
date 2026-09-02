using Formbase.Core.Primitives;
using Formbase.Core.Schema;

namespace Formbase.Core.Tests.Schema;

public class AffixVocabularyTests
{
    [Fact]
    public void Constructor_rejects_an_empty_vocabulary()
    {
        var act = () => new AffixVocabulary(new Dictionary<string, string>());

        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_rejects_a_blank_affix(string? affix)
    {
        var act = () => new AffixVocabulary(new Dictionary<string, string> { [affix!] = "pending" });

        act.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Resolve_returns_null_when_no_declared_affix_matches()
    {
        var vocabulary = new AffixVocabulary(new Dictionary<string, string> { ["-request"] = "pending" });

        vocabulary.Resolve(FormTypeRef.Create("invoice")).Should().BeNull();
    }

    [Fact]
    public void Resolve_returns_the_tag_for_a_matching_suffix()
    {
        // The read, not inferred, cue this targets: the class is in the form name, the action is
        // in the suffix (formbase/docs/CONSTITUTION.md SS1) -- e.g. a Korean "cheongguseo" (invoice
        // request) reads as State=pending purely from its declared name, no inference involved.
        var vocabulary = new AffixVocabulary(new Dictionary<string, string> { ["seo"] = "pending" });

        vocabulary.Resolve(FormTypeRef.Create("invoice-cheongguseo")).Should().Be("pending");
    }

    [Fact]
    public void Resolve_prefers_the_longest_matching_suffix()
    {
        var vocabulary = new AffixVocabulary(new Dictionary<string, string>
        {
            ["seo"] = "pending",
            ["cheongguseo"] = "requested",
        });

        vocabulary.Resolve(FormTypeRef.Create("invoice-cheongguseo")).Should().Be("requested");
    }

    [Fact]
    public void Resolve_matching_is_read_only_and_never_partial_or_fuzzy()
    {
        // A form type that merely contains the affix without ending in it must not match --
        // this is a read of a declared suffix, not a substring guess.
        var vocabulary = new AffixVocabulary(new Dictionary<string, string> { ["seo"] = "pending" });

        vocabulary.Resolve(FormTypeRef.Create("seo-invoice")).Should().BeNull();
    }
}
