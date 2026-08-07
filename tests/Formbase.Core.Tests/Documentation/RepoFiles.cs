namespace Formbase.Core.Tests.Documentation;

/// <summary>
/// Locates the repository from the test binaries, so a gate over the repository's own documents
/// does not depend on the working directory it was run from.
/// <para>
/// It lives on its own because a gate whose subject is a <em>set</em> of files — every document
/// under a directory, rather than one named document — needs the location and not just the
/// contents, and the second copy of a directory walk is where the two start to disagree about
/// what the repository root is.
/// </para>
/// </summary>
internal static class RepoFiles
{
    public static string Root { get; } = FindRoot();

    public static string Read(params string[] relativeParts)
        => File.ReadAllText(Path.Combine([Root, .. relativeParts]));

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Formbase.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName
            ?? throw new InvalidOperationException("Could not locate the repository root (no Formbase.slnx above the test binaries).");
    }
}
