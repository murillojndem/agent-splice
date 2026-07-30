using System.Collections.Frozen;

namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>
/// The top-level request fields AgentSplice models.
/// </summary>
/// <remarks>
/// "Known" means AgentSplice has a concept for the field — a corresponding structural-summary
/// property, or a role in validation — not that OpenAI defines it. <c>seed</c> and
/// <c>response_format</c> are perfectly real OpenAI fields and are recorded as unknown, because
/// AgentSplice genuinely does not model them and claiming otherwise would misreport what it
/// understands.
///
/// Unknown does not mean rejected. Every field outside this set is forwarded verbatim and recorded
/// by name, which is what makes transparent forwarding verifiable (FR-CHAT-004, FR-TRACE-008).
///
/// The set matches the <c>properties</c> of the OpenAPI <c>ChatCompletionRequest</c> schema, and a
/// contract test binds the two so neither can drift.
/// </remarks>
internal static class KnownChatCompletionFields
{
    internal const string Model = "model";
    internal const string Messages = "messages";
    internal const string Stream = "stream";
    internal const string Tools = "tools";
    internal const string ToolChoice = "tool_choice";
    internal const string StreamOptions = "stream_options";

    internal static FrozenSet<string> TopLevel { get; } = new[]
    {
        Model,
        Messages,
        Tools,
        ToolChoice,
        Stream,
        StreamOptions,
        "temperature",
        "top_p",
        "max_tokens",
    }.ToFrozenSet(StringComparer.Ordinal);
}
