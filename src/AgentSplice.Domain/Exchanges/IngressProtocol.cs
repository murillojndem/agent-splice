namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// The client-facing protocol an exchange arrived on (docs/SPECIFICATION.md FR-TRACE-002).
/// </summary>
/// <remarks>
/// Stage 1 supports one ingress protocol. The enum exists so that an exchange records which
/// protocol produced it, because Stage 4A adds Anthropic Messages and a stored exchange must remain
/// interpretable without guessing.
/// </remarks>
public enum IngressProtocol
{
    /// <summary>OpenAI-compatible <c>POST /v1/chat/completions</c>.</summary>
    OpenAiChatCompletions = 1,
}
