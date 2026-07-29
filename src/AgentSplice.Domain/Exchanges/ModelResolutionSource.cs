namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// How a client-visible model identifier was resolved (docs/SPECIFICATION.md FR-MOD-007).
/// </summary>
public enum ModelResolutionSource
{
    /// <summary>A configured alias mapped the identifier to a runtime and upstream model.</summary>
    ConfiguredAlias = 1,

    /// <summary>The identifier was forwarded to the default runtime unchanged.</summary>
    PassThrough = 2,

    /// <summary>The identifier matched a model discovered from a runtime catalogue.</summary>
    Discovered = 3,
}
