using AgentSplice.TestSupport.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// A configured persistence mode that this build does not implement says so at startup.
/// </summary>
/// <remarks>
/// The shipped configuration selects SQLite because FR-DATA-002 makes it the local default when
/// persistence is enabled. No store exists yet, so without this notice an operator reading their own
/// settings would expect a database file and accumulating exchanges, and would get neither with
/// nothing to explain it.
/// </remarks>
public sealed class PersistenceNoticeTests
{
    [Fact]
    public async Task A_configured_but_unimplemented_persistence_mode_is_announced()
    {
        var logs = await StartAsync("Sqlite");

        Assert.Contains(
            logs.Records,
            record => record.EventId.Name == "PersistenceNotImplemented");
    }

    [Fact]
    public async Task The_notice_names_the_configured_mode_and_says_nothing_is_retained()
    {
        var logs = await StartAsync("Sqlite");

        var notice = logs.Records.Single(record => record.EventId.Name == "PersistenceNotImplemented");

        Assert.Contains("Sqlite", string.Join("|", notice.StateValues), StringComparison.Ordinal);
        Assert.Contains("not retained", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ephemeral_operation_produces_no_notice()
    {
        // FR-DATA-001: running without a database is a supported configuration, not a degraded one,
        // so it must not be reported as a problem.
        var logs = await StartAsync("None");

        Assert.DoesNotContain(
            logs.Records,
            record => record.EventId.Name == "PersistenceNotImplemented");
    }

    private static async Task<CapturingLoggerProvider> StartAsync(string mode)
    {
        var logs = new CapturingLoggerProvider();

        await using var factory = new AgentSpliceApplicationFactory(
            new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["agentsplice:persistence:mode"] = mode,
                ["agentsplice:persistence:connectionString"] =
                    mode == "None" ? null : "Data Source=audit-test.db",
            },
            services => services.AddSingleton<ILoggerProvider>(logs));

        // Creating a client boots the host, which is what runs the hosted services.
        using var client = factory.CreateClient();

        return logs;
    }
}
