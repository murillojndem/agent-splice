namespace AgentSplice.Application.Configuration;

/// <summary>
/// What AgentSplice records (docs/SPECIFICATION.md FR-DATA-005, FR-DATA-006, ADR 0004).
/// </summary>
public sealed class CaptureOptions
{
    /// <summary>Whether structural exchange metadata is recorded.</summary>
    public bool MetadataEnabled { get; set; } = true;

    /// <summary>
    /// Whether raw request and response content is recorded. Off by default and required to stay
    /// off unless an operator explicitly opts in (FR-DATA-005). Enabling it means prompts and model
    /// output leave process memory, so sanitisation runs before anything is written (FR-DATA-006).
    /// </summary>
    public bool ContentEnabled { get; set; }

    /// <summary>Retention windows per artifact category.</summary>
    public RetentionOptions Retention { get; set; } = new();
}
