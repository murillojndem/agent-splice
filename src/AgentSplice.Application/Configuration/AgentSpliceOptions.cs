namespace AgentSplice.Application.Configuration;

/// <summary>
/// Root of the AgentSplice configuration tree (docs/SPECIFICATION.md section 12).
/// </summary>
/// <remarks>
/// Bound from the <c>agentsplice</c> section and validated at startup, so an invalid deployment
/// fails before readiness rather than at the first client request (NFR 14.2).
/// </remarks>
public sealed class AgentSpliceOptions
{
    /// <summary>Configuration section name.</summary>
    public const string SectionName = "agentsplice";

    /// <summary>Externally reachable base URL, when AgentSplice sits behind a proxy or in a container.</summary>
    public string? PublicBaseUrl { get; set; }

    /// <summary>Metadata persistence settings.</summary>
    public PersistenceOptions Persistence { get; set; } = new();

    /// <summary>Diagnostic detail settings.</summary>
    public DiagnosticsOptions Diagnostics { get; set; } = new();

    /// <summary>Configured model runtimes.</summary>
    public IList<RuntimeEndpointOptions> Runtimes { get; set; } = new List<RuntimeEndpointOptions>();

    /// <summary>Configured client-visible model aliases.</summary>
    public IList<ModelAliasOptions> Aliases { get; set; } = new List<ModelAliasOptions>();

    /// <summary>What is recorded and for how long.</summary>
    public CaptureOptions Capture { get; set; } = new();

    /// <summary>Compatibility adapter settings.</summary>
    public AdapterOptions Adapters { get; set; } = new();
}
