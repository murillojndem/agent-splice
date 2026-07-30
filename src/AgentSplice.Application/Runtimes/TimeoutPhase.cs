namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Which configured timeout budget elapsed (docs/SPECIFICATION.md FR-CHAT-007, FR-CHAT-008).
/// </summary>
/// <remarks>
/// The phase is reported because a local runtime fails in distinguishable ways: refusing the
/// connection, accepting it and never answering, and answering then stalling. Collapsing them into
/// "timed out" is what makes the real cause unattributable.
/// </remarks>
public enum TimeoutPhase
{
    /// <summary>The upstream connection could not be established in time.</summary>
    Connect = 1,

    /// <summary>The runtime accepted the request but did not send response headers in time.</summary>
    ResponseHeaders = 2,

    /// <summary>The gap between two upstream reads exceeded its budget while a stream was open.</summary>
    IdleStream = 3,

    /// <summary>The total budget for the upstream request elapsed.</summary>
    Total = 4,
}
