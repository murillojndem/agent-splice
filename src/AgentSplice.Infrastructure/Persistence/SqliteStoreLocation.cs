using Microsoft.Data.Sqlite;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Where a SQLite connection string's store is, resolved against the content root (ADR 0017).
/// </summary>
/// <remarks>
/// Microsoft.Data.Sqlite resolves a relative <c>Data Source</c> against the process's working
/// directory, and everything else a deployment configures by relative path — <c>appsettings.json</c>
/// itself, <c>agentsplice:dashboard:rootPath</c> — resolves against the content root. The two agree
/// under <c>dotnet run</c>, where the working directory is the project directory, and in the container,
/// whose store path is absolute. They disagree wherever the process is started from somewhere else: a
/// published executable run from another folder would read its settings beside itself and create its
/// database in whatever directory it was started from, a different one each time.
///
/// So a relative file path is resolved against the content root, and everything that is not a
/// relative file path is returned exactly as written: an absolute path; an empty data source, which
/// SQLite treats as a private temporary database; <c>:memory:</c> or <c>Mode=Memory</c>; a
/// <c>file:</c> URI, which SQLite parses itself; and a path through <c>|DataDirectory|</c>, which
/// names its own base.
/// </remarks>
internal static class SqliteStoreLocation
{
    private const string MemoryDataSource = ":memory:";
    private const string UriPrefix = "file:";
    private const string DataDirectoryMacro = "|DataDirectory|";

    /// <summary>
    /// The connection string with a relative file path resolved against <paramref name="contentRoot"/>;
    /// the same instance when there is nothing to resolve.
    /// </summary>
    internal static string ResolveAgainst(string connectionString, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(connectionString);
        ArgumentException.ThrowIfNullOrWhiteSpace(contentRoot);

        var builder = new SqliteConnectionStringBuilder(connectionString);

        if (!IsRelativeFile(builder))
        {
            return connectionString;
        }

        builder.DataSource = Path.GetFullPath(builder.DataSource, contentRoot);

        return builder.ToString();
    }

    /// <summary>
    /// The file the connection string names, for saying where evidence is kept; the data source as
    /// written when it is not a plain file path.
    /// </summary>
    /// <remarks>
    /// Only the data source, never the connection string, which may carry a password keyword.
    /// </remarks>
    internal static string DescribeStore(string connectionString)
    {
        ArgumentNullException.ThrowIfNull(connectionString);

        return new SqliteConnectionStringBuilder(connectionString).DataSource;
    }

    private static bool IsRelativeFile(SqliteConnectionStringBuilder builder)
    {
        var dataSource = builder.DataSource;

        return builder.Mode != SqliteOpenMode.Memory
            && !string.IsNullOrEmpty(dataSource)
            && !string.Equals(dataSource, MemoryDataSource, StringComparison.OrdinalIgnoreCase)
            && !dataSource.StartsWith(UriPrefix, StringComparison.OrdinalIgnoreCase)
            && !dataSource.StartsWith(DataDirectoryMacro, StringComparison.OrdinalIgnoreCase)
            && !Path.IsPathFullyQualified(dataSource);
    }
}
