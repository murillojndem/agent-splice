namespace AgentSplice.Application.Configuration;

/// <summary>
/// Model discovery policy for one runtime endpoint
/// (docs/SPECIFICATION.md FR-MOD-003, FR-MOD-008).
/// </summary>
public sealed class DiscoveryOptions
{
    /// <summary>Whether models are discovered from this runtime's catalogue.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>How long a discovered catalogue is reused before refreshing.</summary>
    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Whether a stale catalogue is served when refresh fails. Enabled by default so that a briefly
    /// unreachable runtime degrades model discovery rather than emptying the catalogue.
    /// </summary>
    public bool ServeStaleOnFailure { get; set; } = true;

    /// <summary>
    /// Whether AgentSplice sends probe requests to establish model capabilities. Disabled by
    /// default (FR-MOD-008): probing spends real inference time and its results are weaker evidence
    /// than a conformance run.
    /// </summary>
    public bool CapabilityProbingEnabled { get; set; }
}
