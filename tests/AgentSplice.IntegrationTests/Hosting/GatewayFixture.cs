using System.Globalization;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// A running gateway pointed at a running fake upstream.
/// </summary>
/// <remarks>
/// Both halves are real: the production host and a real Kestrel listener. The only thing the tests
/// control is the runtime's base address and what that listener is scripted to answer, so the
/// behaviour under test is the gateway's rather than a mock's.
/// </remarks>
internal sealed class GatewayFixture : IAsyncDisposable
{
    internal const string RuntimeId = "lmstudio-local";

    private readonly AgentSpliceApplicationFactory factory;

    private GatewayFixture(FakeUpstreamServer upstream, AgentSpliceApplicationFactory factory, bool kestrel)
    {
        Upstream = upstream;
        this.factory = factory;

        if (kestrel)
        {
            // Started before the client is created, so the client is given the listener's address
            // and talks to it over a real socket.
            factory.StartServer();
        }

        Client = factory.CreateClient();
    }

    internal FakeUpstreamServer Upstream { get; }

    internal HttpClient Client { get; }

    /// <summary>The host's services, for the few tests that must reach past HTTP — such as flushing telemetry.</summary>
    internal IServiceProvider Services => factory.Services;

    internal static async Task<GatewayFixture> StartAsync(
        Action<Dictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null,
        bool kestrel = false)
    {
        var upstream = await FakeUpstreamServer.StartAsync().ConfigureAwait(false);

        var settings = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["agentsplice:runtimes:0:id"] = RuntimeId,
            ["agentsplice:runtimes:0:provider"] = "lmstudio",
            ["agentsplice:runtimes:0:baseUrl"] = upstream.OpenAiBaseAddress.AbsoluteUri,
            ["agentsplice:runtimes:0:apiKeyEnvironmentVariable"] = null,
            ["agentsplice:runtimes:0:enabled"] = "true",
            ["agentsplice:runtimes:0:discovery:enabled"] = "true",
            ["agentsplice:runtimes:0:discovery:cacheDuration"] = "00:00:30",

        };

        configure?.Invoke(settings);

        var factory = new AgentSpliceApplicationFactory(settings, configureServices, kestrel);

        return new GatewayFixture(upstream, factory, kestrel);
    }

    /// <summary>Configuration key for a property of the runtime at a given index.</summary>
    internal static string RuntimeKey(int index, string property) =>
        string.Format(CultureInfo.InvariantCulture, "agentsplice:runtimes:{0}:{1}", index, property);

    /// <summary>
    /// Sets every timeout phase of the first runtime, for a test that shortens the total budget.
    /// </summary>
    /// <remarks>
    /// No phase may exceed the total, or the host refuses to start, so a test that shortens the total
    /// states every phase rather than inheriting a default it would have to know. Each argument is
    /// required: a phase added later stops every call here from compiling until its test decides the
    /// value.
    /// </remarks>
    internal static void SetTimeouts(
        Dictionary<string, string?> settings,
        string connect,
        string responseHeaders,
        string firstByte,
        string idleStream,
        string total)
    {
        settings[RuntimeKey(0, "timeouts:connect")] = connect;
        settings[RuntimeKey(0, "timeouts:responseHeaders")] = responseHeaders;
        settings[RuntimeKey(0, "timeouts:firstByte")] = firstByte;
        settings[RuntimeKey(0, "timeouts:idleStream")] = idleStream;
        settings[RuntimeKey(0, "timeouts:total")] = total;
    }

    /// <summary>Configuration key for a property of the alias at a given index.</summary>
    internal static string AliasKey(int index, string property) =>
        string.Format(CultureInfo.InvariantCulture, "agentsplice:aliases:{0}:{1}", index, property);

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await factory.DisposeAsync().ConfigureAwait(false);
        await Upstream.DisposeAsync().ConfigureAwait(false);
    }
}
