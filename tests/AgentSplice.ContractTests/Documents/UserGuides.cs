using AgentSplice.TestSupport;

namespace AgentSplice.ContractTests.Documents;

/// <summary>
/// The documents a user follows to install the gateway, configure it, and point a client at it.
/// </summary>
/// <remarks>
/// One list, so that every test that checks what "the user guides" name reads the same files. A guide
/// added to one test and not another would be checked for its settings and not for its error codes,
/// or the other way round, with nothing to say which.
/// </remarks>
internal static class UserGuides
{
    private static readonly string[][] Paths =
    [
        ["README.md"],
        ["docs", "INSTALLATION.md"],
        ["docs", "CLIENTS.md"],
        ["docs", "DEVELOPMENT.md"],
    ];

    /// <summary>Each guide's repository-relative name and its text.</summary>
    internal static IEnumerable<(string Name, string Text)> Read() =>
        Paths.Select(segments => (string.Join('/', segments), RepositoryPaths.ReadText(segments)));
}
