namespace AgentSplice.Domain.Runtimes;

/// <summary>
/// Health of a configured runtime endpoint (docs/SPECIFICATION.md FR-HEALTH-004).
/// </summary>
/// <remarks>
/// The members are deliberately more specific than reachable/unreachable. "The runtime answered but
/// returned an incompatible response" and "the runtime answered but exposes no models" are the two
/// states that a naive health check reports as healthy, and they are the two that actually break an
/// agent client.
/// </remarks>
public enum RuntimeHealthStatus
{
    /// <summary>Health has not been determined yet.</summary>
    Unknown = 1,

    /// <summary>The runtime answered and exposes at least one model.</summary>
    Healthy = 2,

    /// <summary>The runtime could not be reached.</summary>
    Unreachable = 3,

    /// <summary>The runtime rejected the configured credentials.</summary>
    AuthenticationFailed = 4,

    /// <summary>The runtime answered with a response the protocol module cannot interpret.</summary>
    IncompatibleResponse = 5,

    /// <summary>The runtime answered but exposes no models.</summary>
    NoModels = 6,
}
