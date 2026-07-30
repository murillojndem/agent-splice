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

        // The listener is a constructor dependency of the telemetry rather than something resolved
        // from the container at call time, so "spans exist before the first one is started" is a
        // compile-time fact rather than a registration ordering convention.
        services.AddSingleton<AgentSpliceActivityListener>();
        services.AddSingleton<IExchangeTelemetry, ExchangeTelemetry>();

        return services;
    }
}
