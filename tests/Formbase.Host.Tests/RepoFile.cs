namespace Formbase.Host.Tests;

/// <summary>
/// Reads a repository file by its path relative to the repository root, walking up from the test
/// assembly's location so the tests do not depend on the working directory.
/// <para>
/// Shared rather than private to a single test: a gate that compares the served surface to a file
/// checked in beside it needs this, and a second copy is a second place for the search to be
/// changed — after which two gates disagree about where the repository is.
/// </para>
/// </summary>
internal static class RepoFile
{
    public static string Read(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(
                directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{relativePath} not found above {AppContext.BaseDirectory}");
    }
}
