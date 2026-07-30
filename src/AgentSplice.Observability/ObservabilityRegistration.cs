using AgentSplice.Application.Observability;
using Microsoft.Extensions.DependencyInjection;

namespace AgentSplice.Observability;

/// <summary>Registers AgentSplice's spans and metrics.</summary>
public static class ObservabilityRegistration
{
    /// <summary>Registers the telemetry implementation and the listener that makes spans exist.</summary>
    /// <remarks>
    /// The listener is a singleton for the process lifetime. Registering a second one on the same
    /// sources would sample every activity twice, which is why the OpenTelemetry SDK must replace
    /// it rather than join it in a later stage.
    /// </remarks>
    public static IServiceCollection AddAgentSpliceObservability(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddSingleton<AgentSpliceActivityListener>();
        services.AddSingleton<ExchangeTelemetry>();
        services.AddSingleton<IExchangeTelemetry>(provider =>
        {
            // Resolved so the listener is constructed before the first span is started. Without it,
            // StartActivity returns null and no trace identifier ever exists.
            provider.GetRequiredService<AgentSpliceActivityListener>();

            return provider.GetRequiredService<ExchangeTelemetry>();
        });

        return services;
    }
}
