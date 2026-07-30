using Microsoft.Extensions.Logging;

namespace AgentSplice.Application.Diagnostics;

/// <summary>
/// Stable identifiers for the events AgentSplice logs (docs/OBSERVABILITY.md "Structured logs").
/// </summary>
/// <remarks>
/// A log message is prose and will be reworded; an identifier is what an operator's alert rule, log
/// query, or issue template can actually match on. Without one, every wording change silently breaks
/// whatever was watching.
///
/// Ranges are grouped so a reader can tell at a glance which part of the system spoke: 1000 for the
/// request path, 1100 for routing and discovery, 1200 for hosting.
/// </remarks>
public static class GatewayEventIds
{
    /// <summary>A completion request faulted in a way the gateway did not anticipate.</summary>
    public static EventId ExchangeFaulted { get; } = new(1001, nameof(ExchangeFaulted));

    /// <summary>Recording an exchange's evidence failed. The client response is unaffected.</summary>
    public static EventId EvidenceRecordingFailed { get; } = new(1002, nameof(EvidenceRecordingFailed));

    /// <summary>Emitting spans or metrics failed. The client response is unaffected.</summary>
    public static EventId InstrumentationFailed { get; } = new(1003, nameof(InstrumentationFailed));

    /// <summary>A runtime's model catalogue could not be refreshed.</summary>
    public static EventId RuntimeDiscoveryFailed { get; } = new(1101, nameof(RuntimeDiscoveryFailed));

    /// <summary>Building the client-visible model list failed.</summary>
    public static EventId ModelListFailed { get; } = new(1102, nameof(ModelListFailed));

    /// <summary>A runtime names an API key variable that is unset or blank.</summary>
    public static EventId RuntimeCredentialMissing { get; } = new(1103, nameof(RuntimeCredentialMissing));

    /// <summary>A fault escaped the request pipeline entirely.</summary>
    public static EventId UnhandledPipelineFault { get; } = new(1201, nameof(UnhandledPipelineFault));

    /// <summary>A persistence mode is configured that this build does not implement.</summary>
    public static EventId PersistenceNotImplemented { get; } = new(1202, nameof(PersistenceNotImplemented));
}
