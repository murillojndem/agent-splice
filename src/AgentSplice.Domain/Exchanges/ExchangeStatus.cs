namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// Lifecycle state of a completion exchange (docs/SPECIFICATION.md section 13.3).
/// </summary>
public enum ExchangeStatus
{
    /// <summary>The request was accepted but not yet validated or routed.</summary>
    Accepted = 1,

    /// <summary>The model and runtime are resolved and the upstream request is in flight.</summary>
    Forwarding = 2,

    /// <summary>Bytes are being forwarded to the client.</summary>
    Streaming = 3,

    /// <summary>
    /// The transport cycle finished: AgentSplice forwarded a request and relayed an answer.
    /// </summary>
    /// <remarks>
    /// Not a claim that the operation succeeded. A runtime that answers 429 or 500, or reports an
    /// error inside a 200, is relayed verbatim, and the exchange completes with no
    /// <see cref="FailureClass"/>, because AgentSplice did not fail. Whether the runtime succeeded is
    /// its <see cref="UpstreamOutcome"/>, read from its status and from whether it reported an error in
    /// its answer; success and error are classified from that, never from the absence of a failure
    /// class (ADR 0026).
    /// </remarks>
    Completed = 4,

    /// <summary>The client disconnected or cancelled before completion.</summary>
    Cancelled = 5,

    /// <summary>The exchange failed. <see cref="FailureClass"/> records why.</summary>
    Failed = 6,
}
