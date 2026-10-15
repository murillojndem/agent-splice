using AgentSplice.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Xunit;

namespace AgentSplice.UnitTests.Persistence;

/// <summary>
/// A relative SQLite store path resolves against the content root; nothing else is touched
/// (ADR 0017).
/// </summary>
/// <remarks>
/// Every form SQLite gives its own meaning to is listed here and must come back as the same string
/// instance, not merely an equal one: rewriting <c>:memory:</c> into a file path would turn an
/// ephemeral store into a file on disk, and normalising the keywords of a connection string nobody
/// needed to resolve would change what the operator wrote for no reason.
/// </remarks>
public sealed class SqliteStoreLocationTests
{
    private static readonly string ContentRoot = Path.Combine(Path.GetTempPath(), "agentsplice-content-root");

    [Theory]
    [InlineData("Data Source=agentsplice.db", "agentsplice.db")]
    [InlineData("Data Source=data/agentsplice.db", "data/agentsplice.db")]
    [InlineData("Data Source=../agentsplice.db", "../agentsplice.db")]
    [InlineData("DataSource=agentsplice.db", "agentsplice.db")]
    [InlineData("Filename=agentsplice.db", "agentsplice.db")]
    public void A_relative_file_is_resolved_against_the_content_root(string connectionString, string relative)
    {
        var resolved = new SqliteConnectionStringBuilder(SqliteStoreLocation.ResolveAgainst(connectionString, ContentRoot));

        Assert.Equal(Path.GetFullPath(relative, ContentRoot), resolved.DataSource);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=shared;Mode=Memory;Cache=Shared")]
    [InlineData("Data Source=file:agentsplice.db?mode=ro")]
    [InlineData("Data Source=|DataDirectory|agentsplice.db")]
    [InlineData("Data Source=")]
    [InlineData("")]
    public void A_form_sqlite_gives_its_own_meaning_is_returned_as_written(string connectionString)
    {
        Assert.Same(connectionString, SqliteStoreLocation.ResolveAgainst(connectionString, ContentRoot));
    }

    [Fact]
    public void An_absolute_path_is_returned_as_written()
    {
        var connectionString = "Data Source=" + Path.Combine(Path.GetTempPath(), "elsewhere", "agentsplice.db");

        Assert.Same(connectionString, SqliteStoreLocation.ResolveAgainst(connectionString, ContentRoot));
    }

    [Fact]
    public void Resolving_the_path_keeps_every_other_setting()
    {
        var resolved = new SqliteConnectionStringBuilder(
            SqliteStoreLocation.ResolveAgainst(
                "Data Source=agentsplice.db;Mode=ReadWriteCreate;Cache=Shared;Pooling=False;Default Timeout=5",
                ContentRoot));

        Assert.Equal(Path.GetFullPath("agentsplice.db", ContentRoot), resolved.DataSource);
        Assert.Equal(SqliteOpenMode.ReadWriteCreate, resolved.Mode);
        Assert.Equal(SqliteCacheMode.Shared, resolved.Cache);
        Assert.False(resolved.Pooling);
        Assert.Equal(5, resolved.DefaultTimeout);
    }

    [Fact]
    public void Describing_a_store_names_its_file_and_nothing_else()
    {
        // The description is logged at startup. A connection string can carry a password; the file
        // name is all an operator needs to find their evidence.
        var description = SqliteStoreLocation.DescribeStore(@"Data Source=C:\data\agentsplice.db;Password=not-for-logs");

        Assert.Equal(@"C:\data\agentsplice.db", description);
        Assert.DoesNotContain("not-for-logs", description, StringComparison.Ordinal);
    }
}
