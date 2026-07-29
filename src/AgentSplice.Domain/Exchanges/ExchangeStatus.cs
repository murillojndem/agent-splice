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

    /// <summary>The exchange finished normally.</summary>
    Completed = 4,

    /// <summary>The client disconnected or cancelled before completion.</summary>
    Cancelled = 5,

    /// <summary>The exchange failed. <see cref="FailureClass"/> records why.</summary>
    Failed = 6,
}
