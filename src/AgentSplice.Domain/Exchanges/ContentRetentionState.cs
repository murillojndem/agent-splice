namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// What, if anything, was retained for an exchange
/// (docs/SPECIFICATION.md FR-TRACE-010, FR-DATA-005, ADR 0004).
/// </summary>
/// <remarks>
/// Every exchange carries this state so that a reader never has to infer whether raw content might
/// exist. <see cref="Disabled"/> is the default for a Stage 1 deployment.
/// </remarks>
public enum ContentRetentionState
{
    /// <summary>Nothing was retained: capture is switched off entirely.</summary>
    Disabled = 1,

    /// <summary>Structural metadata was retained; no request or response content was stored.</summary>
    MetadataOnly = 2,

    /// <summary>Sanitised content was retained under an explicit opt-in.</summary>
    SanitizedContent = 3,

    /// <summary>Retained content passed its retention window and was removed.</summary>
    Expired = 4,

    /// <summary>Retained content was deleted on request.</summary>
    Deleted = 5,
}
