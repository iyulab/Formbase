using System.Text.RegularExpressions;

namespace Formbase.Core.Tests.Documentation;

/// <summary>
/// Holds the documents' statement of which version they describe.
/// <para>
/// The repository's default branch is what a reader lands on, and it is ahead of what they can
/// install. That is not a drift between two files, so nothing that compares one document to
/// another can see it: the HTTP reference can be exactly right about the host in this tree and
/// still describe a surface no released artifact carries — which is the case today, since the host
/// is neither a published package nor a pushed image.
/// </para>
/// <para>
/// The convention is a <c>Since x.y.z</c> marker on anything a release does not carry yet, naming
/// a Formbase version. It is one-directional by nature — nothing can find a behaviour someone
/// forgot to mark — so what is held here is the half that can be: a marker names a version the
/// released one has not reached, and the version the documents call released is the one a push
/// would publish. The released version is read from the build property rather than named here,
/// because that property is the one value a release cannot forget to change: pushing it
/// <em>is</em> the release.
/// </para>
/// </summary>
public class DocsVersionMarkerTests
{
    [Fact]
    public void No_marker_names_a_version_the_released_one_has_already_reached()
    {
        var released = ReleasedVersion();

        var stale = MarkedFiles()
            .SelectMany(file => Regex.Matches(File.ReadAllText(file), @"\*\*Since (?<v>\d+\.\d+\.\d+)")
                .Select(m => (File: Path.GetFileName(file), Version: Version.Parse(m.Groups["v"].Value))))
            .Where(m => m.Version <= released)
            .Select(m => $"{m.File}: Since {m.Version}")
            .Distinct(StringComparer.Ordinal)
            .OrderBy(m => m, StringComparer.Ordinal)
            .ToList();

        string.Join(Environment.NewLine, stale).Should().BeEmpty(
            $"the released version is {released}, so a marker at or below it describes something a "
            + "reader can already install — the marker has done its work and belongs in the release "
            + "that shipped it, not in the document afterwards");
    }

    [Fact]
    public void The_version_the_reference_calls_released_is_the_one_a_push_would_publish()
    {
        var reference = RepoFiles.Read("docs", "API.md");

        Regex.Match(reference, @"released version is \*\*(?<v>\d+\.\d+\.\d+)\*\*").Groups["v"].Value
            .Should().Be(ReleasedVersion().ToString(),
                "a reader takes that sentence as the answer to \"what can I install instead\", and "
                + "it is the one statement here that no other gate compares to anything");

        Regex.Match(reference, @"at `v(?<v>\d+\.\d+\.\d+)`").Groups["v"].Value
            .Should().Be(ReleasedVersion().ToString(),
                "the tag it sends a reader to for released documentation has to be the released one");
    }

    /// <summary>
    /// The version a push publishes — the head of the chain, and the only link in it that cannot be
    /// forgotten. <see cref="ReadmeInstallParityTests"/> already holds the README's two statements
    /// of it to this same property, so all four move together.
    /// </summary>
    private static Version ReleasedVersion()
        => Version.Parse(Regex.Match(
            RepoFiles.Read("Directory.Build.props"),
            @"<Version>(?<v>\d+\.\d+\.\d+)</Version>").Groups["v"].Value);

    /// <summary>
    /// Every document a reader meets before the source: the front page and the reference tree.
    /// Taken as a directory rather than a list so a document added later is covered by existing.
    /// </summary>
    private static IEnumerable<string> MarkedFiles()
        => new[] { Path.Combine(RepoFiles.Root, "README.md") }
            .Concat(Directory.GetFiles(Path.Combine(RepoFiles.Root, "docs"), "*.md"));
}
