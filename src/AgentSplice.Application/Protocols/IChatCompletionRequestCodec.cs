using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// Reads and rewrites a completion request in the ingress protocol's own shape.
/// </summary>
/// <remarks>
/// Reading and rewriting live on one port because they share a fact: the byte offsets of the
/// <c>model</c> value, established by the same single pass that produced the structural summary.
/// Splitting them would mean parsing the body twice, and the second parse could disagree with the
/// first.
/// </remarks>
public interface IChatCompletionRequestCodec
{
    /// <summary>True when a request declared a media type this protocol's body is sent as.</summary>
    /// <remarks>
    /// Asked before the body is read. A request with no <c>Content-Type</c>, or with one a browser can
    /// send from any web page without asking — a form, or plain text — is refused, which is what stops
    /// another site's page from running inference through a loopback gateway unseen (ADR 0027).
    /// </remarks>
    bool AcceptsMediaType(string? contentType);

    /// <summary>Parses and validates a request body without reading its content.</summary>
    ChatCompletionReadResult Read(ReadOnlySpan<byte> body);

    /// <summary>
    /// Produces the body to forward, substituting the model identifier.
    /// </summary>
    /// <remarks>
    /// Only the bytes of the <c>model</c> value change. Every other byte of the original request —
    /// including escape forms, number formatting, property order, and insignificant whitespace — is
    /// copied through unchanged, so "nothing else was modified" is a claim about bytes rather than
    /// about the parser's opinion of them.
    /// </remarks>
    byte[] SubstituteModel(ReadOnlySpan<byte> body, ChatCompletionEnvelope envelope, UpstreamModelId model);
}
