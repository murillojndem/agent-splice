using System.Collections.Frozen;

namespace AgentSplice.Application.Errors;

/// <summary>
/// Stable client-facing error codes (docs/API.md "Stable error codes").
/// </summary>
/// <remarks>
/// These strings are a public contract: clients, conformance reports, and issue templates match on
/// them. They are never derived from an upstream message, and they never carry a credential, a
/// hostname, or model output. Only the Stage 1 core codes are declared here; later-stage codes are
/// added by the slice that can actually emit them.
///
/// <see cref="AgentSplice.Domain.Exchanges.FailureClass"/> is the internal vocabulary; the mapping
/// between the two is introduced with the Stage 1A error translation slice.
/// </remarks>
public static class ErrorCodes
{
    /// <summary>The ingress envelope was not valid for the declared protocol.</summary>
    public const string InvalidRequest = "agentsplice_invalid_request";

    /// <summary>The requested client-visible model does not resolve.</summary>
    public const string ModelNotFound = "agentsplice_model_not_found";

    /// <summary>The resolved runtime endpoint is not configured.</summary>
    public const string RuntimeNotFound = "agentsplice_runtime_not_found";

    /// <summary>The runtime endpoint could not be reached.</summary>
    public const string RuntimeUnavailable = "agentsplice_runtime_unavailable";

    /// <summary>The runtime endpoint rejected the configured credentials.</summary>
    public const string RuntimeAuthenticationFailed = "agentsplice_runtime_authentication_failed";

    /// <summary>A configured timeout phase elapsed.</summary>
    public const string UpstreamTimeout = "agentsplice_upstream_timeout";

    /// <summary>The upstream response body was not valid for the protocol.</summary>
    public const string InvalidUpstreamResponse = "agentsplice_invalid_upstream_response";

    /// <summary>The upstream event stream violated SSE framing or protocol rules.</summary>
    public const string InvalidUpstreamStream = "agentsplice_invalid_upstream_stream";

    /// <summary>The client cancelled or disconnected.</summary>
    public const string RequestCancelled = "agentsplice_request_cancelled";

    /// <summary>The gateway is already serving as many completions as it will serve at once.</summary>
    public const string GatewayOverloaded = "agentsplice_gateway_overloaded";

    /// <summary>Metadata persistence was unavailable.</summary>
    public const string PersistenceUnavailable = "agentsplice_persistence_unavailable";

    /// <summary>An unexpected gateway fault.</summary>
    public const string InternalError = "agentsplice_internal_error";

    /// <summary>Every Stage 1 core error code. Verified against docs/API.md by a contract test.</summary>
    public static FrozenSet<string> Core { get; } = new[]
    {
        InvalidRequest,
        ModelNotFound,
        RuntimeNotFound,
        RuntimeUnavailable,
        RuntimeAuthenticationFailed,
        UpstreamTimeout,
        InvalidUpstreamResponse,
        InvalidUpstreamStream,
        RequestCancelled,
        GatewayOverloaded,
        PersistenceUnavailable,
        InternalError,
    }.ToFrozenSet(StringComparer.Ordinal);
}
