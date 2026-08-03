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
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string Readme = File.ReadAllText(Path.Combine(RepoRoot, "README.md"));

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Formbase.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (no Formbase.slnx above the test binaries).");
    }

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
