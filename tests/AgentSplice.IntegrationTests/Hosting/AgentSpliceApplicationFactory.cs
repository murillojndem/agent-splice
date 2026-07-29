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
    private readonly IReadOnlyDictionary<string, string?> overrides;
    private readonly Action<IServiceCollection>? configureServices;

    internal AgentSpliceApplicationFactory(
        IReadOnlyDictionary<string, string?>? overrides = null,
        Action<IServiceCollection>? configureServices = null)
    {
        this.overrides = overrides ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        this.configureServices = configureServices;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(overrides));

        if (configureServices is { } configure)
        {
            builder.ConfigureTestServices(configure);
        }
    }
}
