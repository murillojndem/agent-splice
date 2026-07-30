namespace AgentSplice.Application.Configuration;

/// <summary>
/// How strictly the ingress protocol is enforced (docs/API.md "Compatibility policy").
/// </summary>
public sealed class CompatibilityOptions
{
    /// <summary>
    /// What happens to top-level request fields AgentSplice does not model. Transparent by default.
    /// </summary>
    public CompatibilityMode UnsupportedFields { get; set; } = CompatibilityMode.Transparent;
}
