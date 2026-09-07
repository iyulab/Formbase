using System.Text.RegularExpressions;
using Formbase.Core.Ports;

namespace Formbase.Core.Tests.Documentation;

/// <summary>
/// The README's architecture section enumerates the ports and counts them. Both are facts the
/// assembly already holds — a port is a public interface in the <c>Formbase.Core.Ports</c>
/// namespace, and there are as many as there are — so both can drift, and the count did: a ninth
/// port was added and the sentence kept saying eight for two releases. An identifier list is the
/// kind of claim a gate can hold exactly, so this holds it.
/// </summary>
public sealed class ReadmeArchitectureParityTests
{
    private static readonly string Readme = RepoFiles.Read("README.md");

    private static readonly string[] CountWords =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve",
    ];

    private static IReadOnlyList<string> DeclaredPorts()
        => typeof(IRawStore).Assembly.GetTypes()
            .Where(t => t.IsInterface && t.IsPublic && t.Namespace == typeof(IRawStore).Namespace)
            .Select(t => t.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

    private static string ArchitectureSection()
    {
        var match = Regex.Match(Readme, @"^## Architecture\s*$(?<body>.*?)(?=^## )", RegexOptions.Multiline | RegexOptions.Singleline);
        match.Success.Should().BeTrue("the README has an Architecture section");
        return match.Groups["body"].Value;
    }

    [Fact]
    public void The_port_table_lists_every_port_and_nothing_else()
    {
        var listed = Regex.Matches(ArchitectureSection(), @"^\| `(?<port>I[A-Za-z0-9]+)` \|", RegexOptions.Multiline)
            .Select(m => m.Groups["port"].Value)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        listed.Should().Equal(DeclaredPorts(),
            "a port missing from the table is a seam a reader does not know they can plug into, and a "
            + "row naming no port sends them after an interface that does not exist");
    }

    [Fact]
    public void The_sentence_that_counts_the_ports_counts_them()
    {
        var stated = Regex.Match(ArchitectureSection(), @"^(?<word>[A-Z][a-z]+) ports define the engine", RegexOptions.Multiline);
        stated.Success.Should().BeTrue("the architecture section opens by counting the ports");

        var expected = CountWords[DeclaredPorts().Count];
        stated.Groups["word"].Value.Should().BeEquivalentTo(expected,
            "the count is read before the table and a reader who trusts it stops looking after that many rows");
    }
}
