namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Which configured timeout budget elapsed (docs/SPECIFICATION.md FR-CHAT-007, FR-CHAT-008).
/// </summary>
/// <remarks>
/// The phase is reported because a local runtime fails in distinguishable ways: refusing the
/// connection, accepting it and never answering, starting a stream and never sending its first byte,
/// and answering then stalling. Collapsing them into "timed out" is what makes the real cause
/// unattributable.
/// </remarks>
public enum TimeoutPhase
{
    /// <summary>The upstream connection could not be established in time.</summary>
    Connect = 1,

    /// <summary>The runtime accepted the request but did not send response headers in time.</summary>
    ResponseHeaders = 2,

    /// <summary>
    /// The runtime sent a stream's response headers but not the first byte of its body in time
    /// (ADR 0028).
    /// </summary>
    FirstByte = 5,

    /// <summary>The gap between two upstream reads after a stream's first byte exceeded its budget.</summary>
    IdleStream = 3,

    /// <summary>The total budget for the upstream request elapsed.</summary>
    Total = 4,
}
