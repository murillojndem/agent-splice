namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// How a streamed exchange ended (docs/SPECIFICATION.md FR-STR-011).
/// </summary>
/// <remarks>
/// The distinct members matter for diagnosis: a client disconnect, an upstream close, a timeout, and
/// a malformed event all look like "the stream stopped" from the outside, and collapsing them is
/// what makes local-runtime failures unattributable.
/// </remarks>
public enum StreamTermination
{
    /// <summary>The exchange was not streamed.</summary>
    NotApplicable = 1,

    /// <summary>The stream ended but AgentSplice cannot classify how.</summary>
    Unknown = 2,

    /// <summary>The upstream stream ended normally without a protocol terminator.</summary>
    NormalCompletion = 3,

    /// <summary>The protocol terminator was observed, for example the OpenAI <c>[DONE]</c> sentinel.</summary>
    ProtocolTerminatorReceived = 4,

    /// <summary>The client cancelled or disconnected.</summary>
    ClientCancelled = 5,

    /// <summary>The upstream runtime cancelled or aborted the response.</summary>
    UpstreamCancelled = 6,

    /// <summary>A configured timeout phase elapsed.</summary>
    Timeout = 7,

    /// <summary>An event violated SSE framing or contained an unparsable payload.</summary>
    MalformedEvent = 8,

    /// <summary>The connection was lost before the stream ended.</summary>
    ConnectionLost = 9,

    /// <summary>A configured AgentSplice bound was exceeded, so relaying stopped.</summary>
    /// <remarks>
    /// Deliberately distinct from <see cref="MalformedEvent"/>. That one describes what the runtime
    /// did; this one describes what AgentSplice decided. Reporting a gateway policy stop as runtime
    /// misbehaviour misattributes the cause, which is the class of misleading evidence this product
    /// exists to remove.
    /// </remarks>
    LimitExceeded = 10,
}
