using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// A completion request as the transport layer received it.
/// </summary>
/// <remarks>
/// Carries the raw bytes rather than a parsed shape, because the application decides what to parse
/// and because the forwarded body must be able to be these exact bytes. Carries only the headers the
/// gateway acts on: there is deliberately no field for the client's <c>Authorization</c>, so it
/// cannot be forwarded upstream by accident (docs/SECURITY.md).
/// </remarks>
public sealed record ChatCompletionRequest
{
    private ChatCompletionRequest()
    {
    }

    /// <summary>The body exactly as received.</summary>
    public ReadOnlyMemory<byte> Body { get; private init; }

    /// <summary>The correlation token, resolved from the client's header or minted.</summary>
    public PublicRequestId RequestId { get; private init; }

    /// <summary>When the transport accepted the request, before the body was read.</summary>
    /// <remarks>
    /// Taken by the transport rather than by the gateway, because the gateway first sees the request
    /// only after the body has been read. Stamping acceptance then would fold the read into whatever
    /// phase follows and make a slow upload look like slow validation.
    /// </remarks>
    public DateTimeOffset AcceptedAt { get; private init; }

    /// <summary>When the request body finished being read.</summary>
    public DateTimeOffset BodyReadAt { get; private init; }

    /// <summary>Creates an ingress request.</summary>
    public static ChatCompletionRequest Create(
        ReadOnlyMemory<byte> body,
        PublicRequestId requestId,
        DateTimeOffset acceptedAt,
        DateTimeOffset bodyReadAt)
    {
        if (requestId.IsEmpty)
        {
            throw new ArgumentException("A request requires a correlation token.", nameof(requestId));
        }

        return new ChatCompletionRequest
        {
            Body = body,
            RequestId = requestId,
            AcceptedAt = acceptedAt,
            BodyReadAt = bodyReadAt,
        };
    }
}
