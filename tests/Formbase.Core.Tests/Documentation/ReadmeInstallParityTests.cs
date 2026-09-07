using System.Text.RegularExpressions;

namespace Formbase.Core.Tests.Documentation;

/// <summary>
/// The README's install section makes three claims a reader will act on: which packages exist,
/// which version they are at, and which MorphDB line they pair with. Each is a fact the repository
/// already holds elsewhere, so each can drift — and the last one already cost a downstream consumer
/// real time, reconstructing the pair from the changelog because the README never stated it.
/// Individual doc fixes have not held; this is the gate instead.
/// </summary>
public sealed class ReadmeInstallParityTests
{
    private static readonly string RepoRoot = RepoFiles.Root;
    private static readonly string Readme = RepoFiles.Read("README.md");

    [Fact]
    public void The_advertised_version_is_the_version_that_would_be_published()
    {
        var props = File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props"));
        var declared = Regex.Match(props, @"<Version>(?<v>[^<]+)</Version>").Groups["v"].Value;
        declared.Should().NotBeEmpty();

        var advertised = Regex.Match(Readme, @"Current release: \*\*(?<v>[^*]+)\*\*").Groups["v"].Value;

        advertised.Should().Be(declared,
            "a bump publishes that version, so the README states it or it states something false");
    }

    [Fact]
    public void The_pair_statement_names_the_same_version_the_install_section_does()
    {
        // The release version is stated twice in the same section — once as the current release and
        // again inside the compatibility pair. Only the first was held, so the second could go stale
        // on its own, which is how a reader ends up pinning a version the pair line disagrees with.
        var declared = Regex.Match(
            File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props")),
            @"<Version>(?<v>[^<]+)</Version>").Groups["v"].Value;

        var inPair = Regex.Match(Readme, @"`Formbase\.\* (?<v>\d+\.\d+\.\d+)` pairs with").Groups["v"].Value;

        inPair.Should().Be(declared,
            "the pair line carries the same fact as `Current release:` and drifts unless it is held too");
    }

    [Fact]
    public void The_image_tag_the_install_section_pulls_is_the_released_version()
    {
        // The install section states the release a third time, thirty lines below the other two:
        // as the tag on the published image. The two statements above were held and this one was
        // not, and it is the one a reader pastes into a terminal — so it went stale across a release
        // while the sentence beside it was correct.
        var declared = Regex.Match(
            File.ReadAllText(Path.Combine(RepoRoot, "Directory.Build.props")),
            @"<Version>(?<v>[^<]+)</Version>").Groups["v"].Value;

        var pulled = Regex.Matches(Readme, @"ghcr\.io/iyulab/formbase:(?<v>\d+\.\d+\.\d+)")
            .Select(m => m.Groups["v"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        pulled.Should().NotBeEmpty("the install section tells a reader which image to pull");
        pulled.Should().AllBe(declared,
            "a bump publishes that image tag, so an example naming another tag pulls the previous release");
    }

    [Fact]
    public void Every_packable_project_is_listed_and_nothing_else_is()
    {
        var packable = Directory
            .EnumerateFiles(Path.Combine(RepoRoot, "src"), "*.csproj", SearchOption.AllDirectories)
            .Where(p => !File.ReadAllText(p).Contains("<IsPackable>false</IsPackable>", StringComparison.Ordinal))
            .Select(Path.GetFileNameWithoutExtension)
            .ToHashSet(StringComparer.Ordinal);

        var advertised = Regex.Matches(Readme, @"dotnet add package (?<id>Formbase\.\S+)")
            .Select(m => m.Groups["id"].Value)
            .ToHashSet(StringComparer.Ordinal);

        advertised.Should().BeEquivalentTo(packable,
            "an unlisted package is invisible to consumers and a listed non-package is a 404");
    }

    [Fact]
    public void The_advertised_morphdb_pair_is_the_line_the_live_suite_actually_runs()
    {
        var fixture = File.ReadAllText(Path.Combine(
            RepoRoot, "tests", "Formbase.Core.Tests", "Live", "MorphDb", "MorphDbFixture.cs"));
        var pinned = Regex.Match(fixture, @"ghcr\.io/iyulab/morphdb:(?<v>\d+\.\d+\.\d+)").Groups["v"].Value;
        pinned.Should().NotBeEmpty("the fixture pins an explicit server image");

        var advertised = Regex.Match(Readme, @"MorphDB `(?<v>\d+\.\d+)\.x`").Groups["v"].Value;
        advertised.Should().NotBeEmpty("the README states the compatible pair");

        pinned.Should().StartWith(advertised + ".",
            "the pair the README promises is only credible if it is the one the contract is tested against");
    }
}
