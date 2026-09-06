using System.Text.RegularExpressions;

namespace Formbase.Core.Tests;

/// <summary>
/// This repository is published. Everything committed to it — source, tests, documentation, project
/// files — is read by people outside the organisation that develops it, and text written for an
/// internal reader does not become internal again by sitting in a comment.
/// </summary>
/// <remarks>
/// <para>
/// The tokens checked here are the ones a machine can judge without context: identifiers that only
/// resolve inside a private tracker or planning document, paths into a private working directory
/// that is not part of this repository at all, and absolute paths off a developer's machine. They
/// leak the same way every time — a change is written while its planning item or design note is on
/// screen, and that document's path goes into the comment explaining the change. A review reads the
/// diff for correctness and slides straight past it, which is why this is checked rather than
/// remembered.
/// </para>
/// <para>
/// Deliberately narrow: a person's name, an internal host, a project code word all need context to
/// judge, and a check that cries wolf gets switched off. What remains is mechanical — a match means
/// the text points at something no outside reader can open, and it should say what it means instead.
/// </para>
/// </remarks>
public class InternalTokenLeakTests
{
    private static readonly string[] SearchedExtensions =
        [".cs", ".md", ".csproj", ".props", ".slnx", ".yml", ".yaml"];

    private static readonly string[] SkippedDirectories =
        [".git", "bin", "obj", "node_modules", "TestResults", "coverage", "claudedocs"];

    /// <summary>Read once for the whole class; each pattern is a separate case over the same text.</summary>
    private static readonly Lazy<IReadOnlyList<(string Path, string[] Lines)>> Corpus = new(() =>
    {
        var root = RepoRoot();

        return PublishedFiles(root)
            // This file names every pattern it looks for, so it would always match itself.
            .Where(f => !string.Equals(
                Path.GetFileName(f), "InternalTokenLeakTests.cs", StringComparison.Ordinal))
            .Select(f => (Path.GetRelativePath(root, f), File.ReadAllLines(f)))
            .ToList();
    });

    public static TheoryData<string, string> Patterns() => new()
    {
        // A backlog or tracker item id.
        { "internal backlog or ticket id", @"\b(BD-\d{8}-\d+|HD-\d+|P\d+-[a-z]\b)" },

        // A development cycle number — an artefact of how the work is scheduled, not something a
        // reader of the published tree can resolve.
        { "internal cycle number", @"\bcycle-\d+\b" },

        // The private working directory, and the issue-draft file names that live in it.
        { "internal working document", @"\bclaudedocs\b|\bISSUE-[A-Za-z0-9][A-Za-z0-9-]*\.md\b" },

        // A path that only exists on the machine it was written on.
        { "absolute local path", @"(?<![A-Za-z0-9])[A-Za-z]:\\|/home/[a-z]|/Users/[A-Za-z]" },
    };

    [Theory]
    [MemberData(nameof(Patterns))]
    public void No_published_text_names_something_only_an_insider_can_look_up(string what, string pattern)
    {
        var regex = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(5));
        var corpus = Corpus.Value;

        // A scan that resolved the wrong root would read nothing and pass forever — the one way this
        // check could be worse than not existing. Hold it to actually reaching the tree.
        corpus.Should().HaveCountGreaterThan(50, "the scan must cover the repository, not an empty directory");
        corpus.Should().Contain(f => f.Path.EndsWith("README.md", StringComparison.Ordinal));

        var leaks = (from file in corpus
                     from index in Enumerable.Range(0, file.Lines.Length)
                     let hit = regex.Match(file.Lines[index])
                     where hit.Success
                     select $"{file.Path}:{index + 1}: {hit.Value.Trim()}").ToList();

        string.Join(Environment.NewLine, leaks).Should().BeEmpty(
            "a published file must not carry {0} — say what the thing means instead of pointing at a "
            + "document only the authors can open", what);
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Formbase.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException($"no repository root above {AppContext.BaseDirectory}");
    }

    /// <summary>
    /// Walks down pruning as it goes rather than enumerating everything and filtering after — build
    /// output alone is tens of thousands of files, and this runs in a suite whose value is answering
    /// quickly.
    /// </summary>
    private static IEnumerable<string> PublishedFiles(string root)
    {
        var pending = new Stack<string>([root]);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (!SkippedDirectories.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase))
                {
                    pending.Push(child);
                }
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (SearchedExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                {
                    yield return file;
                }
            }
        }
    }
}
