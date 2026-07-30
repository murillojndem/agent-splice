using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Models;
using AgentSplice.Application.Runtimes;
using AgentSplice.Infrastructure.Persistence;
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

        // Stage 1A stores nothing, so evidence is handed to a sink that discards it. Stage 1C
        // replaces this registration with the metadata store; nothing else has to change.
        services.AddSingleton<IExchangeRecordSink, NullExchangeRecordSink>();

        // Says at startup that a configured persistence mode is not implemented, so an operator
        // reading their own settings is not left expecting a database that never appears.
        services.AddHostedService<UnimplementedPersistenceNotice>();

        // These resolve the protocol ports, so an ingress protocol module must also be registered.
        services.AddSingleton<ModelListGateway>();
        services.AddSingleton<ChatCompletionStreamRelay>();
        services.AddSingleton<ChatCompletionGateway>();

        return services;
    }
}
