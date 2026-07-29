using AgentSplice.Application.Models;
using AgentSplice.Application.Runtimes;
using AgentSplice.Infrastructure.Runtimes;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSplice.Infrastructure.Composition;

/// <summary>
/// Registers the routing and model-catalogue services the request path depends on.
/// </summary>
/// <remarks>
/// Singletons throughout. The registries project validated configuration once at startup, and the
/// discovery cache must outlive a request or its window and its refresh coalescing would both be
/// meaningless.
/// </remarks>
public static class AgentSpliceRequestPathRegistration
{
    /// <summary>Registers model discovery, catalogue composition, and model resolution.</summary>
    public static IServiceCollection AddAgentSpliceRequestPath(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<IRuntimeApiKeyResolver, EnvironmentRuntimeApiKeyResolver>();
        services.AddSingleton<RuntimeRegistry>();
        services.AddSingleton<ModelAliasRegistry>();
        services.AddSingleton<ModelRuntimeProviderRegistry>();
        services.AddSingleton<ModelDiscoveryCache>();
        services.AddSingleton<ModelCatalogueService>();
        services.AddSingleton<ModelResolver>();

        // Resolves the protocol writer ports, so an ingress protocol module must also be registered.
        services.AddSingleton<ModelListGateway>();

        return services;
    }
}
