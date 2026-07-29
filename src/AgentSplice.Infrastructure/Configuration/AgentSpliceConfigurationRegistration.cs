using AgentSplice.Application.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AgentSplice.Infrastructure.Configuration;

/// <summary>
/// Binds and validates <see cref="AgentSpliceOptions"/> for a host.
/// </summary>
public static class AgentSpliceConfigurationRegistration
{
    /// <summary>
    /// Registers the AgentSplice configuration tree with startup validation.
    /// </summary>
    /// <remarks>
    /// <c>ValidateOnStart</c> is deliberate: NFR 14.2 requires configuration to be validated before
    /// readiness. Without it an invalid runtime URL or a dangling alias would surface as a confusing
    /// failure on the first client request instead of as a startup error naming the setting.
    /// </remarks>
    public static IServiceCollection AddAgentSpliceConfiguration(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<AgentSpliceOptions>, AgentSpliceOptionsValidator>();

        services
            .AddOptions<AgentSpliceOptions>()
            .Bind(configuration.GetSection(AgentSpliceOptions.SectionName))
            .ValidateOnStart();

        return services;
    }
}
