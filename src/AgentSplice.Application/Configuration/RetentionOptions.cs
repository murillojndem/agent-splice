namespace AgentSplice.Application.Configuration;

/// <summary>
/// Retention windows per artifact category (docs/SPECIFICATION.md FR-DATA-007, section 16.2).
/// </summary>
/// <remarks>
/// Metadata and content are separate categories with separate windows (FR-DATA-004). Content, if it
/// is captured at all, expires much sooner than metadata, because metadata is the durable evidence
/// and content is the sensitive part.
/// </remarks>
public sealed class RetentionOptions
{
    /// <summary>How long structural exchange metadata is kept.</summary>
    public TimeSpan Metadata { get; set; } = TimeSpan.FromDays(30);

    /// <summary>How long sanitised content is kept, when content capture is enabled at all.</summary>
    public TimeSpan Content { get; set; } = TimeSpan.FromDays(1);
}
