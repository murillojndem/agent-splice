namespace AgentSplice.Application.Configuration;

/// <summary>
/// One administratively configured model runtime (docs/SPECIFICATION.md sections 12 and 13.1).
/// </summary>
/// <remarks>
/// Runtime endpoints are configured by an operator and never supplied per request (NFR 14.3). The
/// API key is referenced by environment-variable name, never embedded, so a profile file can be
/// committed or shared without leaking a credential.
/// </remarks>
public sealed class RuntimeEndpointOptions
{
    /// <summary>Stable identifier used in routing, traces, and metric dimensions.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Human-readable name for diagnostics and the dashboard.</summary>
    public string? DisplayName { get; set; }

    /// <summary>Provider adapter key, for example <c>lmstudio</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>Absolute base URL of the runtime's OpenAI-compatible surface.</summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>
    /// Name of the environment variable holding the API key. The value itself is never stored in
    /// configuration, exchanges, or replay artifacts (FR-DATA-010).
    /// </summary>
    public string? ApiKeyEnvironmentVariable { get; set; }

    /// <summary>Whether this runtime participates in routing and discovery.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Model discovery policy.</summary>
    public DiscoveryOptions Discovery { get; set; } = new();

    /// <summary>Timeout phases.</summary>
    public TimeoutOptions Timeouts { get; set; } = new();
}
