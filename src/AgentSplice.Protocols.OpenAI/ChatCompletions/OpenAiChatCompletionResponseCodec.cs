using System.Text.Json;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;

namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>
/// Extracts structural evidence from an OpenAI chat completion response.
/// </summary>
/// <remarks>
/// Reads for evidence only. The body reaches the client verbatim whether or not this succeeds, so an
/// unreadable body costs a structural summary and nothing else.
/// </remarks>
public sealed class OpenAiChatCompletionResponseCodec : IChatCompletionResponseCodec
{
    /// <inheritdoc />
    public ChatCompletionResponseFacts Read(ReadOnlySpan<byte> body, string? mediaType)
    {
        if (body.IsEmpty)
        {
            return ChatCompletionResponseFacts.Uninterpretable;
        }

        try
        {
            var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);

            if (!JsonDocument.TryParseValue(ref reader, out var document))
            {
                return ChatCompletionResponseFacts.Uninterpretable;
            }

            using (document)
            {
                return document.RootElement.ValueKind == JsonValueKind.Object
                    ? Read(document.RootElement, body.Length)
                    : ChatCompletionResponseFacts.Uninterpretable;
            }
        }
        catch (JsonException)
        {
            return ChatCompletionResponseFacts.Uninterpretable;
        }
    }

    private static ChatCompletionResponseFacts Read(JsonElement root, int bodyLength)
    {
        var choiceCount = 0;
        var toolCallCount = 0;
        var finishReasons = new List<string>();

        if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
        {
            foreach (var choice in choices.EnumerateArray())
            {
                choiceCount++;
                ReadChoice(choice, finishReasons, ref toolCallCount);
            }
        }

        var usage = ReadUsage(root);

        var summary = StructuralResponseSummary.Create(
            choiceCount,
            finishReasons,
            toolCallCount,
            bodyLength,

            // A non-streamed exchange has no events. Zero here is a defined fact, not a placeholder.
            streamEventCount: 0,
            usageReported: !usage.IsUnknown);

        return new ChatCompletionResponseFacts(summary, usage);
    }

    private static void ReadChoice(JsonElement choice, List<string> finishReasons, ref int toolCallCount)
    {
        if (choice.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
        {
            finishReasons.Add(reason.GetString()!);
        }

        // Counted only where the protocol puts structured tool calls. Stage 1 never infers a tool
        // call from prose, because a model printing tool syntax is not a model that made one
        // (FR-CHAT-014).
        if (choice.TryGetProperty("message", out var message)
            && message.ValueKind == JsonValueKind.Object
            && message.TryGetProperty("tool_calls", out var toolCalls)
            && toolCalls.ValueKind == JsonValueKind.Array)
        {
            toolCallCount += toolCalls.GetArrayLength();
        }
    }

    /// <summary>
    /// Reads the usage object, keeping each component independently optional.
    /// </summary>
    /// <remarks>
    /// A runtime that reports no usage yields <see cref="UsageObservation.Unknown"/>, never zeroes:
    /// zero is a claim that no tokens were consumed. The reported total is read rather than computed,
    /// because a runtime may count tokens AgentSplice cannot see.
    /// </remarks>
    private static UsageObservation ReadUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
        {
            return UsageObservation.Unknown;
        }

        return UsageObservation.Create(
            ReadTokenCount(usage, "prompt_tokens"),
            ReadTokenCount(usage, "completion_tokens"),
            ReadTokenCount(usage, "total_tokens"));
    }

    private static TokenCount? ReadTokenCount(JsonElement usage, string propertyName) =>
        usage.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt32(out var tokens)
        && tokens >= 0
            ? TokenCount.FromUpstream(tokens)
            : null;
}
