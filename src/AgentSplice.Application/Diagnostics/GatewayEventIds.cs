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
/// request path, 1100 for routing and discovery, 1200 for hosting, 1300 for persistence and
/// retention.
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

    /// <summary>The bounded metadata queue was full, so an exchange's evidence was dropped.</summary>
    public static EventId MetadataQueueSaturated { get; } = new(1301, nameof(MetadataQueueSaturated));

    /// <summary>Writing a batch of exchange metadata to the store failed.</summary>
    public static EventId MetadataPersistenceFailed { get; } = new(1302, nameof(MetadataPersistenceFailed));

    /// <summary>A retention sweep finished; the message carries what it removed.</summary>
    public static EventId RetentionSweepCompleted { get; } = new(1303, nameof(RetentionSweepCompleted));

    /// <summary>A retention sweep could not complete.</summary>
    public static EventId RetentionSweepFailed { get; } = new(1304, nameof(RetentionSweepFailed));
}
