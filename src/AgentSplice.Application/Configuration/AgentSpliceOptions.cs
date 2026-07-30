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

    /// <summary>
    /// Runtime that receives a model identifier matching no alias and no discovered model
    /// (<see cref="Domain.Exchanges.ModelResolutionSource.PassThrough"/>).
    /// </summary>
    /// <remarks>
    /// Unset by default, so the strict posture — an unrecognised model is rejected — is what a
    /// deployment gets without asking. Setting it is a deliberate operator decision to let the
    /// runtime be the authority on its own model names, which is also the only way to route to a
    /// runtime that has discovery disabled and no aliases.
    /// </remarks>
    public string? DefaultRuntimeId { get; set; }

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

    /// <summary>Size bounds on request and upstream bodies.</summary>
    public LimitsOptions Limits { get; set; } = new();

    /// <summary>How strictly the ingress protocol is enforced.</summary>
    public CompatibilityOptions Compatibility { get; set; } = new();

    /// <summary>Compatibility adapter settings.</summary>
    public AdapterOptions Adapters { get; set; } = new();
}
