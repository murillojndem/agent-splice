using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// Hosts the real <c>AgentSplice.Api</c> application with configuration overrides.
/// </summary>
/// <remarks>
/// The tests deliberately boot the production host rather than a hand-assembled service collection,
/// because the property under test is that a misconfigured deployment fails at startup
/// (NFR 14.2). A hand-assembled container would not exercise <c>ValidateOnStart</c>.
/// </remarks>
internal sealed class AgentSpliceApplicationFactory : WebApplicationFactory<Program>
{
    private readonly IReadOnlyDictionary<string, string?> overrides;

    internal AgentSpliceApplicationFactory(IReadOnlyDictionary<string, string?>? overrides = null) =>
        this.overrides = overrides ?? new Dictionary<string, string?>(StringComparer.Ordinal);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.UseEnvironment("Test");
        builder.ConfigureAppConfiguration(
            (_, configuration) => configuration.AddInMemoryCollection(overrides));
    }
}
