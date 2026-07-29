namespace AgentSplice.Application.Configuration;

/// <summary>
/// How much header detail is retained (docs/SECURITY.md, docs/SPECIFICATION.md section 16).
/// </summary>
public enum HeaderCaptureMode
{
    /// <summary>No headers are retained.</summary>
    None = 0,

    /// <summary>Only headers on the configured allowlist are retained. The default.</summary>
    Allowlist = 1,

    /// <summary>
    /// All headers are retained. Never a default: authorization and cookie headers would be
    /// captured, so this requires an explicit operator decision.
    /// </summary>
    All = 2,
}
