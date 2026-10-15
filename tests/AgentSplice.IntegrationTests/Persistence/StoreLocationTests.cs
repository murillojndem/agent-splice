using System.Globalization;
using AgentSplice.Application.Diagnostics;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport;
using AgentSplice.TestSupport.Logging;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Persistence;

/// <summary>
/// A relative store path resolves against the content root, as every other relative path in the
/// settings does, and the gateway says where the store is (ADR 0017, FR-DATA-002).
/// </summary>
/// <remarks>
/// The test host's content root is the Api project and its working directory is the test output
/// folder, so a relative path names two different files depending on which one it is read against —
/// the same trick that proves the dashboard's <c>rootPath</c> rule. SQLite left to itself reads it
/// against the working directory; the gateway must not.
/// </remarks>
public sealed class StoreLocationTests
{
    [Fact]
    public void A_relative_store_path_is_resolved_against_the_content_root_and_reported()
    {
        // Under the test output, so that the relative path from the content root stays on one drive.
        var directory = Path.Combine(
            AppContext.BaseDirectory,
            "store-location-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));
        Directory.CreateDirectory(directory);

        var expected = Path.Combine(directory, "agentsplice.db");
        var contentRoot = RepositoryPaths.Resolve("src", "AgentSplice.Api");
        var relative = Path.GetRelativePath(contentRoot, expected);
        var fromWorkingDirectory = Path.GetFullPath(relative, Environment.CurrentDirectory);

        Assert.False(Path.IsPathRooted(relative));
        Assert.NotEqual(expected, fromWorkingDirectory);

        var logs = new CapturingLoggerProvider();

        try
        {
            using (var factory = new AgentSpliceApplicationFactory(
                new Dictionary<string, string?>(StringComparer.Ordinal)
                {
                    ["agentsplice:persistence:mode"] = "Sqlite",
                    ["agentsplice:persistence:connectionString"] = "Data Source=" + relative,
                },
                services => services.AddSingleton<ILoggerProvider>(logs)))
            {
                using var client = factory.CreateClient();
            }

            Assert.True(File.Exists(expected), "The store must be created under the content root.");
            Assert.False(File.Exists(fromWorkingDirectory), "The store must not be created under the working directory.");

            var opened = Assert.Single(logs.Records, record => record.EventId == GatewayEventIds.MetadataStoreOpened);

            Assert.Equal(LogLevel.Information, opened.Level);
            Assert.Contains(opened.AllText(), text => text.Contains(expected, StringComparison.Ordinal));
        }
        finally
        {
            // The provider pools connections, and on Windows an open handle keeps the file locked.
            SqliteConnection.ClearAllPools();

            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException)
            {
                // A leftover temporary directory is not worth failing a passing test over.
            }
        }
    }

    [Fact]
    public void A_deployment_that_retains_nothing_reports_no_store()
    {
        // The shipped test host runs with persistence off, which is a supported deployment: it must
        // neither create a file nor claim a store exists (FR-DATA-001).
        var logs = new CapturingLoggerProvider();

        using (var factory = new AgentSpliceApplicationFactory(configureServices: services => services.AddSingleton<ILoggerProvider>(logs)))
        {
            using var client = factory.CreateClient();
        }

        Assert.DoesNotContain(logs.Records, record => record.EventId == GatewayEventIds.MetadataStoreOpened);
    }
}
