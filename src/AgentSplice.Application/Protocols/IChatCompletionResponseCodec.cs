using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// Extracts structural evidence from a completion response.
/// </summary>
/// <remarks>
/// Reading is for evidence only and never gates forwarding: the body reaches the client verbatim
/// whether or not it can be interpreted. A runtime answering <c>429 text/plain</c> is still
/// answering, and replacing that with a gateway error would discard the most actionable diagnostic
/// a user has.
/// </remarks>
public interface IChatCompletionResponseCodec
{
    /// <summary>Reads what can be established about a response body.</summary>
    ChatCompletionResponseFacts Read(ReadOnlySpan<byte> body, string? mediaType);
}

/// <summary>
/// What a response body yielded, if anything.
/// </summary>
/// <param name="Summary">The structural summary, or <c>null</c> when the body was not interpretable.</param>
/// <param name="Usage">Token usage, always carrying provenance. Unknown when none was reported.</param>
public sealed record ChatCompletionResponseFacts(
    StructuralResponseSummary? Summary,
    UsageObservation Usage)
{
    /// <summary>Nothing could be established from the body.</summary>
    public static ChatCompletionResponseFacts Uninterpretable { get; } =
        new(Summary: null, UsageObservation.Unknown);

    /// <summary>True when the body could be read as protocol data.</summary>
    public bool WasInterpretable => Summary is not null;
}
