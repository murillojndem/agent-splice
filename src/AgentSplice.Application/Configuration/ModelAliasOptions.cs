namespace AgentSplice.Application.Configuration;

/// <summary>
/// A client-visible model alias (docs/SPECIFICATION.md sections 12 and 13.2, FR-MOD-005).
/// </summary>
/// <remarks>
/// An alias is routing configuration, not a semantic transformation. Applying one still produces an
/// explicit routing observation, because FR-TRACE-007 requires every routing change to be visible.
/// </remarks>
public sealed class ModelAliasOptions
{
    /// <summary>The identifier clients see and send.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>The runtime endpoint this alias routes to.</summary>
    public string RuntimeId { get; set; } = string.Empty;

    /// <summary>The model identifier sent upstream.</summary>
    public string UpstreamModelId { get; set; } = string.Empty;

    /// <summary>Whether the alias is offered and resolvable.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Ordering hint when several aliases could serve the same purpose. Lower sorts first.</summary>
    public int Priority { get; set; }
}
