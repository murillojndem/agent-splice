namespace AgentSplice.Application.Configuration;

/// <summary>
/// How much header detail is retained (docs/SECURITY.md, docs/SPECIFICATION.md section 16).
/// </summary>
/// <remarks>
/// Only <see cref="None"/> is accepted in this build, which retains no header (ADR 0027). The values a
/// record keeps from headers — the request id and the runtime's media type — are named fields, not
/// captured headers.
/// </remarks>
public enum HeaderCaptureMode
{
    /// <summary>No headers are retained. The default, and the only mode this build accepts.</summary>
    None = 0,

    /// <summary>Only allowlisted headers are retained. Refused in this build.</summary>
    Allowlist = 1,

    /// <summary>
    /// All headers are retained. Refused in this build, and never to be a default: authorization and
    /// cookie headers would be captured.
    /// </summary>
    All = 2,
}
