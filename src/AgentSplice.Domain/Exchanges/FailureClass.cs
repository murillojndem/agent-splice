namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// Why an exchange failed, expressed as a stable class rather than a transport detail
/// (docs/SPECIFICATION.md section 13.3, docs/API.md stable error codes).
/// </summary>
/// <remarks>
/// Members map one-to-one onto the stable client-facing error codes. The mapping itself is
/// introduced with the Stage 1A error translation slice; this enum is the durable vocabulary the
/// mapping will consume.
/// </remarks>
public enum FailureClass
{
    /// <summary>The ingress envelope was not valid for the declared protocol.</summary>
    InvalidRequest = 1,

    /// <summary>The requested client-visible model does not resolve.</summary>
    ModelNotFound = 2,

    /// <summary>The resolved runtime endpoint is not configured.</summary>
    RuntimeNotFound = 3,

    /// <summary>The runtime endpoint could not be reached.</summary>
    RuntimeUnavailable = 4,

    /// <summary>The runtime endpoint rejected the credentials.</summary>
    RuntimeAuthenticationFailed = 5,

    /// <summary>A configured timeout phase elapsed (FR-CHAT-007, FR-CHAT-008).</summary>
    UpstreamTimeout = 6,

    /// <summary>The upstream response body was not valid for the protocol.</summary>
    InvalidUpstreamResponse = 7,

    /// <summary>The upstream event stream violated SSE framing or protocol rules (FR-STR-007).</summary>
    InvalidUpstreamStream = 8,

    /// <summary>The client cancelled or disconnected.</summary>
    RequestCancelled = 9,

    /// <summary>Metadata persistence was unavailable (FR-DATA-009).</summary>
    PersistenceUnavailable = 10,

    /// <summary>An unexpected gateway fault.</summary>
    InternalError = 11,

    /// <summary>The gateway is already serving as many completions as it will serve at once.</summary>
    /// <remarks>
    /// Distinct from every other member in one respect that matters to a client: it describes a
    /// condition the caller can act on by slowing down, rather than a failure it can only report.
    /// </remarks>
    GatewayOverloaded = 12,
}
