using AgentSplice.Application.Errors;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// Everything the transport layer needs to answer a client, produced entirely by the application.
/// </summary>
/// <remarks>
/// Exists so that endpoints stay a transport concern: they read a request into a value, hand it to
/// the application, and write what comes back. Nothing about status selection, payload shape, or
/// correlation is decided in an endpoint (CLAUDE.md: no domain or orchestration logic in endpoint
/// lambdas).
/// </remarks>
public sealed record GatewayResponse
{
    private GatewayResponse()
    {
    }

    /// <summary>The status to send.</summary>
    public int StatusCode { get; private init; }

    /// <summary>The media type of <see cref="Body"/>.</summary>
    public string MediaType { get; private init; } = string.Empty;

    /// <summary>The bytes to send.</summary>
    public ReadOnlyMemory<byte> Body { get; private init; }

    /// <summary>The correlation token returned as <c>x-agentsplice-request-id</c>.</summary>
    public PublicRequestId RequestId { get; private init; }

    /// <summary>The runtime that served the request, when one was resolved.</summary>
    public RuntimeEndpointId? Runtime { get; private init; }

    /// <summary>The error being reported, or <c>null</c> for a successful response.</summary>
    public GatewayError? Error { get; private init; }

    /// <summary>Creates a successful response.</summary>
    public static GatewayResponse Success(
        int statusCode,
        string mediaType,
        ReadOnlyMemory<byte> body,
        PublicRequestId requestId,
        RuntimeEndpointId? runtime = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        return new GatewayResponse
        {
            StatusCode = statusCode,
            MediaType = mediaType,
            Body = body,
            RequestId = requestId,
            Runtime = runtime,
        };
    }

    /// <summary>Creates an error response, taking its status from the error itself.</summary>
    public static GatewayResponse Failure(
        GatewayError error,
        string mediaType,
        ReadOnlyMemory<byte> body,
        PublicRequestId requestId,
        RuntimeEndpointId? runtime = null)
    {
        ArgumentNullException.ThrowIfNull(error);
        ArgumentException.ThrowIfNullOrWhiteSpace(mediaType);

        return new GatewayResponse
        {
            StatusCode = error.StatusCode,
            MediaType = mediaType,
            Body = body,
            RequestId = requestId,
            Runtime = runtime,
            Error = error,
        };
    }
}
