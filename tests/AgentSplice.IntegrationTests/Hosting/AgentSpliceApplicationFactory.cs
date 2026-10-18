using System.Net;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// Hosts the real <c>AgentSplice.Api</c> application with configuration and service overrides.
/// </summary>
/// <remarks>
/// The tests deliberately boot the production host rather than a hand-assembled service collection,
/// because the properties under test — that a misconfigured deployment fails at startup (NFR 14.2),
/// and that the composition root wires the request path correctly — are properties of the host. A
/// hand-assembled container would exercise neither.
///
/// The service hook exists for the few substitutions a test genuinely needs, such as a controllable
/// clock. It is deliberately not used to replace the request path itself.
/// </remarks>
internal sealed class AgentSpliceApplicationFactory : WebApplicationFactory<Program>
{
    private static readonly Dictionary<string, string?> EphemeralPersistence =
        new(StringComparer.Ordinal)
        {
            ["agentsplice:persistence:mode"] = "None",
            ["agentsplice:persistence:connectionString"] = null,
        };

    private readonly IReadOnlyDictionary<string, string?> overrides;
    private readonly Action<IServiceCollection>? configureServices;

    /// <param name="overrides">Configuration applied over the shipped settings.</param>
    /// <param name="configureServices">The few service substitutions a test genuinely needs.</param>
    /// <param name="kestrel">
    /// Host the gateway on a real Kestrel listener on the IPv4 loopback, on a port the system picks,
    /// rather than in memory. The in-memory server sends a response's headers as soon as they are
    /// committed and Kestrel sends them with the first body write, which is the difference ADR 0019
    /// found and only a real listener can show (ADR 0025).
    /// </param>
    internal AgentSpliceApplicationFactory(
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null,
        bool kestrel = false)
    {
        this.overrides = overrides ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        this.configureServices = configureServices;

        if (kestrel)
        {
            UseKestrel(options => options.Listen(IPAddress.Loopback, 0));
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration((_, configuration) =>
        {
            // Applied under the caller's overrides, so a test that wants a store still gets one.
            // Without it every test that boots the host would inherit the shipped SQLite default and
            // create a database file in the test output directory — shared between test classes that
            // run in parallel, and left behind afterwards.
            configuration.AddInMemoryCollection(EphemeralPersistence);
            configuration.AddInMemoryCollection(overrides);
        });

        if (configureServices is { } configure)
        {
            builder.ConfigureTestServices(configure);
        }
    }
}
