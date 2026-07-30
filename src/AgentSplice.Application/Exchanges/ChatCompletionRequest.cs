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

    /// <summary>Creates an ingress request.</summary>
    public static ChatCompletionRequest Create(ReadOnlyMemory<byte> body, PublicRequestId requestId)
    {
        if (requestId.IsEmpty)
        {
            throw new ArgumentException("A request requires a correlation token.", nameof(requestId));
        }

        return new ChatCompletionRequest { Body = body, RequestId = requestId };
    }
}
