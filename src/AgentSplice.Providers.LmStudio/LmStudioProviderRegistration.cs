using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Registers the LM Studio provider and its per-runtime HTTP clients.
/// </summary>
/// <remarks>
/// A client per runtime rather than one shared client, for two reasons.
///
/// <see cref="SocketsHttpHandler.ConnectTimeout"/> is a property of the handler, not of a request,
/// while <c>timeouts:connect</c> is configured per runtime. One shared handler could honour only a
/// single connect budget, so every other runtime's configured value would silently not apply.
///
/// It also isolates connection pools, so a runtime that is stalling or saturating its connections
/// cannot starve another runtime of them.
///
/// Clients are configured lazily, by name, rather than enumerated at registration time. Reading the
/// runtime list during registration would bind configuration before a host has finished assembling
/// its sources, which is exactly the ordering hazard that made the Stage 0 loopback default a
/// fallback rather than a settings value (ADR 0007).
/// </remarks>
public static class LmStudioProviderRegistration
{
    /// <summary>The <c>provider</c> configuration value this module serves.</summary>
    public const string ProviderKey = "lmstudio";

    internal const string ClientNamePrefix = "agentsplice.lmstudio.";

    /// <summary>The named client serving one runtime.</summary>
    public static string ClientNameFor(RuntimeEndpointId runtime) => ClientNamePrefix + runtime.Value;

    /// <summary>Registers the provider and the configuration its clients need.</summary>
    public static IServiceCollection AddLmStudioProvider(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient();
        services.AddSingleton<IModelRuntimeProvider, LmStudioModelRuntimeProvider>();
        services.AddSingleton<IConfigureOptions<HttpClientFactoryOptions>, LmStudioHttpClientConfigurator>();

        return services;
    }
}
