using AgentSplice.Application.Diagnostics;
using AgentSplice.TestSupport.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// What a binding beyond loopback exposes, said where an operator reads startup (ADR 0027).
/// </summary>
/// <remarks>
/// The administrative token protects <c>/api/v1</c>, and the documents used to promise authentication
/// on any network binding. <c>/v1</c> authenticates no one, so an operator who set a token could
/// believe a network-reachable gateway was closed.
/// </remarks>
public sealed class NetworkExposureNoticeTests : IDisposable
{
    private const string TokenVariable = "AGENTSPLICE_TEST_EXPOSURE_TOKEN";

    public NetworkExposureNoticeTests() =>
        Environment.SetEnvironmentVariable(TokenVariable, "SENTINEL-EXPOSURE-TOKEN");

    public void Dispose() => Environment.SetEnvironmentVariable(TokenVariable, null);

    [Fact]
    public async Task A_gateway_that_listens_beyond_loopback_says_that_v1_authenticates_no_one()
    {
        var logs = await StartAsync(new Dictionary<string, string?>
        {
            ["urls"] = "http://0.0.0.0:5280",
            ["agentsplice:administration:apiKeyEnvironmentVariable"] = TokenVariable,
        });

        var notice = Assert.Single(logs.Records, record => record.EventId.Id == GatewayEventIds.InferenceReachableBeyondLoopback.Id);

        Assert.Equal(LogLevel.Warning, notice.Level);
        Assert.Contains("/v1 authenticates no one", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SENTINEL-EXPOSURE-TOKEN", notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_gateway_on_the_loopback_default_says_nothing()
    {
        var logs = await StartAsync(new Dictionary<string, string?>());

        Assert.DoesNotContain(logs.Records, record => record.EventId.Id == GatewayEventIds.InferenceReachableBeyondLoopback.Id);
    }

    private static async Task<CapturingLoggerProvider> StartAsync(Dictionary<string, string?> settings)
    {
        var logs = new CapturingLoggerProvider();

        await using var factory = new AgentSpliceApplicationFactory(
            settings,
            services => services.AddSingleton<ILoggerProvider>(logs));

        // Starts the host. The in-process server binds nothing, but the notice reads the same
        // configuration the guard does, and that is what decides it.
        using var client = factory.CreateClient();

        return logs;
    }
}
