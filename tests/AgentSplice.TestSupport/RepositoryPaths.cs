namespace AgentSplice.TestSupport;

/// <summary>
/// Locates repository files that tests assert against, such as the specification documents and the
/// OpenAPI draft.
/// </summary>
/// <remarks>
/// Contract tests compare code constants against the documents that declare them. That only works if
/// the documents can be found from a test binary, whose working directory is a build output folder.
/// </remarks>
public static class RepositoryPaths
{
    private const string SolutionFileName = "AgentSplice.sln";

    /// <summary>The repository root directory.</summary>
    /// <exception cref="InvalidOperationException">The root could not be located.</exception>
    public static DirectoryInfo Root { get; } = LocateRoot();

    /// <summary>Resolves a path relative to the repository root.</summary>
    public static string Resolve(params string[] relativeSegments)
    {
        ArgumentNullException.ThrowIfNull(relativeSegments);
        return Path.Combine([Root.FullName, .. relativeSegments]);
    }

    /// <summary>Reads a repository file relative to the root.</summary>
    public static string ReadText(params string[] relativeSegments)
    {
        var path = Resolve(relativeSegments);

        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                FormattableString.Invariant($"Expected repository file '{path}' to exist."),
                path);
        }

        return File.ReadAllText(path);
    }

    private static DirectoryInfo LocateRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFileName)))
            {
                return directory;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            FormattableString.Invariant(
                $"Could not locate '{SolutionFileName}' above '{AppContext.BaseDirectory}'."));
    }
}
