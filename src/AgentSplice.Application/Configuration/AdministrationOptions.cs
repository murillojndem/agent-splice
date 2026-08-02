namespace AgentSplice.Application.Configuration;

/// <summary>
/// Who may read the administrative surface (FR-HEALTH-006, docs/SECURITY.md).
/// </summary>
/// <remarks>
/// The administrative APIs serve stored evidence: structural summaries, timings, runtime
/// configuration, and the model identifiers a client asked for. docs/SECURITY.md requires them to be
/// treated as sensitive even in local deployments, and FR-HEALTH-006 requires authentication once the
/// gateway is bound beyond loopback.
/// </remarks>
public sealed class AdministrationOptions
{
    /// <summary>
    /// Name of the environment variable holding the bearer token, never the token itself.
    /// </summary>
    /// <remarks>
    /// The same shape as <see cref="RuntimeEndpointOptions.ApiKeyEnvironmentVariable"/>, for the same
    /// reason: a secret in a settings file is a secret in source control, in a container image, and
    /// in every diagnostic bundle that copies the configuration. docs/SECURITY.md allows a static
    /// token for Stage 1 and requires that only a reference be stored.
    /// </remarks>
    public string? ApiKeyEnvironmentVariable { get; set; }
}
