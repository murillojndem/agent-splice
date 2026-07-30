using System.Collections.Frozen;

namespace AgentSplice.Application.Errors;

/// <summary>
/// The <c>error.type</c> vocabulary of the OpenAI-shaped error envelope (docs/API.md).
/// </summary>
/// <remarks>
/// The specification supplies exactly one of these by example (<c>upstream_protocol_error</c>,
/// section 10.3), so Stage 1A defines the rest. Two rules shaped the set.
///
/// Client-validation failures reuse OpenAI's own <c>invalid_request_error</c>, including
/// model-not-found, which OpenAI also reports that way with a distinguishing <c>code</c>. An
/// existing SDK that branches on <c>type</c> therefore keeps working against AgentSplice.
///
/// Everything a plain model provider has no vocabulary for — an unreachable runtime, a phase
/// timeout, a gateway configuration defect — gets an AgentSplice-specific category, because
/// flattening them into <c>api_error</c> would discard the one distinction a user needs: which side
/// of the gateway failed.
///
/// <see cref="ErrorCodes"/> remains the stable machine-readable identity; the type is the coarse
/// category a client switches on.
///
/// There is deliberately no type for "the runtime returned a non-2xx". Such a response is relayed
/// verbatim with the runtime's own body, so AgentSplice writes no envelope and there is nothing for
/// a type to describe. Declaring one would publish a category no code path can emit.
/// </remarks>
public static class ErrorTypes
{
    /// <summary>The request was not valid for the declared protocol.</summary>
    public const string InvalidRequest = "invalid_request_error";

    /// <summary>The gateway is configured in a way that cannot serve the request.</summary>
    public const string Configuration = "configuration_error";

    /// <summary>The runtime could not be reached.</summary>
    public const string UpstreamUnavailable = "upstream_unavailable_error";

    /// <summary>The runtime rejected the gateway's credentials.</summary>
    public const string UpstreamAuthentication = "upstream_authentication_error";

    /// <summary>A configured timeout phase elapsed.</summary>
    public const string UpstreamTimeout = "upstream_timeout_error";

    /// <summary>The runtime's answer violated the protocol. The one type the specification names.</summary>
    public const string UpstreamProtocol = "upstream_protocol_error";

    /// <summary>The client disconnected before the exchange completed.</summary>
    public const string Cancellation = "cancellation_error";

    /// <summary>An unexpected gateway fault.</summary>
    public const string Internal = "internal_error";

    /// <summary>Every declared type. Verified against docs/API.md by a contract test.</summary>
    public static FrozenSet<string> All { get; } = new[]
    {
        InvalidRequest,
        Configuration,
        UpstreamUnavailable,
        UpstreamAuthentication,
        UpstreamTimeout,
        UpstreamProtocol,
        Cancellation,
        Internal,
    }.ToFrozenSet(StringComparer.Ordinal);
}
