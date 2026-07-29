namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Immutable projection of <see cref="Configuration.DiscoveryOptions"/> for one runtime
/// (docs/SPECIFICATION.md FR-MOD-003, FR-MOD-008).
/// </summary>
/// <remarks>
/// Projected rather than passed through so that request-path code cannot hold a mutable options
/// instance whose values could change beneath it mid-exchange.
/// </remarks>
public sealed record RuntimeDiscoveryPolicy
{
    private RuntimeDiscoveryPolicy()
    {
    }

    /// <summary>Whether models are discovered from this runtime's catalogue.</summary>
    public bool Enabled { get; private init; }

    /// <summary>How long a discovered catalogue is reused before refreshing.</summary>
    public TimeSpan CacheDuration { get; private init; }

    /// <summary>Whether a stale catalogue is served when a refresh fails.</summary>
    public bool ServeStaleOnFailure { get; private init; }

    /// <summary>Whether AgentSplice sends probe requests to establish model capabilities.</summary>
    public bool CapabilityProbingEnabled { get; private init; }

    /// <summary>Creates a validated discovery policy.</summary>
    public static RuntimeDiscoveryPolicy Create(
        bool enabled,
        TimeSpan cacheDuration,
        bool serveStaleOnFailure,
        bool capabilityProbingEnabled = false)
    {
        if (cacheDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cacheDuration),
                cacheDuration,
                "A cache duration cannot be negative.");
        }

        return new RuntimeDiscoveryPolicy
        {
            Enabled = enabled,
            CacheDuration = cacheDuration,
            ServeStaleOnFailure = serveStaleOnFailure,
            CapabilityProbingEnabled = capabilityProbingEnabled,
        };
    }
}
