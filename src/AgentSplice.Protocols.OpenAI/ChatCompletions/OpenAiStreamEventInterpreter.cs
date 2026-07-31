using System.Text.Json;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;

namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>
/// Interprets the events of an OpenAI-compatible chat completion stream.
/// </summary>
/// <remarks>
/// Evidence only. Every byte has already reached the client before it reaches this class, so a
/// payload this cannot parse costs a structural summary and nothing else — exactly as an unparsable
/// buffered body does.
/// </remarks>
public sealed class OpenAiStreamEventInterpreter : IStreamEventInterpreter
{
    /// <inheritdoc />
    public string StreamMediaType => OpenAiMediaTypes.EventStream;

    /// <inheritdoc />
    public bool MatchesStreamMediaType(string? contentType) => OpenAiMediaTypes.IsEventStream(contentType);

    /// <inheritdoc />
    public IStreamEventInterpreterState Begin() => new State();

    private sealed class State : IStreamEventInterpreterState
    {
        private static ReadOnlySpan<byte> DoneSentinel => "[DONE]"u8;

        private readonly List<string> finishReasons = [];
        private int choiceCount;
        private int toolCallCount;
        private bool sawSemanticOutput;

        public UsageObservation Usage { get; private set; } = UsageObservation.Unknown;

        public StreamEventFacts Interpret(ReadOnlySpan<byte> eventName, ReadOnlySpan<byte> data)
        {
            if (data.IsEmpty)
            {
                // A comment or keepalive. Not malformed: the grammar allows an event with no data,
                // and calling it a protocol violation would blame a runtime for holding a connection
                // open the way the specification tells it to.
                return default;
            }

            // Recognised only as the whole value. A chunk whose content happens to be the text
            // "[DONE]" is a model writing those six characters, not a runtime ending a stream, and
            // treating the two alike would truncate a legitimate response (FR-STR-009).
            if (data.SequenceEqual(DoneSentinel))
            {
                return new StreamEventFacts { IsProtocolTerminator = true };
            }

            try
            {
                var reader = new Utf8JsonReader(data, isFinalBlock: true, state: default);

                if (!JsonDocument.TryParseValue(ref reader, out var document))
                {
                    return new StreamEventFacts { IsMalformed = true };
                }

                using (document)
                {
                    return document.RootElement.ValueKind == JsonValueKind.Object
                        ? ReadChunk(document.RootElement)
                        : new StreamEventFacts { IsMalformed = true };
                }
            }
            catch (JsonException)
            {
                return new StreamEventFacts { IsMalformed = true };
            }
        }

        public StructuralResponseSummary Summarise(int responseBodyBytes, int streamEventCount) =>
            StructuralResponseSummary.Create(
                choiceCount,
                finishReasons,
                toolCallCount,
                responseBodyBytes,
                streamEventCount,
                usageReported: !Usage.IsUnknown);

        private StreamEventFacts ReadChunk(JsonElement root)
        {
            var carriedOutput = false;
            var toolCallsStarted = 0;

            if (root.TryGetProperty("choices", out var choices) && choices.ValueKind == JsonValueKind.Array)
            {
                var index = 0;

                foreach (var choice in choices.EnumerateArray())
                {
                    index++;
                    ReadChoice(choice, ref carriedOutput, ref toolCallsStarted);
                }

                // The widest chunk wins. A terminal usage-only chunk carries an empty array, and
                // taking the last count would erase what the response actually produced.
                choiceCount = Math.Max(choiceCount, index);
            }

            ReadUsage(root);

            toolCallCount += toolCallsStarted;

            var firstOutput = carriedOutput && !sawSemanticOutput;
            sawSemanticOutput |= carriedOutput;

            return new StreamEventFacts
            {
                IsFirstSemanticOutput = firstOutput,
                NativeToolCallsStarted = toolCallsStarted,
            };
        }

        private void ReadChoice(JsonElement choice, ref bool carriedOutput, ref int toolCallsStarted)
        {
            if (choice.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            if (choice.TryGetProperty("finish_reason", out var reason) && reason.ValueKind == JsonValueKind.String)
            {
                var value = reason.GetString()!;

                if (!finishReasons.Contains(value, StringComparer.Ordinal))
                {
                    finishReasons.Add(value);
                }
            }

            if (!choice.TryGetProperty("delta", out var delta) || delta.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            // A role announcement is not output. The first chunk of nearly every OpenAI-compatible
            // stream is exactly that, and counting it would make time to first token measure time to
            // first chunk instead (FR-STR-012).
            if (delta.TryGetProperty("content", out var content)
                && content.ValueKind == JsonValueKind.String
                && content.GetString() is { Length: > 0 })
            {
                carriedOutput = true;
            }

            if (!delta.TryGetProperty("tool_calls", out var toolCalls)
                || toolCalls.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            foreach (var call in toolCalls.EnumerateArray())
            {
                // Counted where the protocol puts structured calls, and only on the fragment that
                // opens one. Continuation fragments carry argument text without an id, so counting
                // every fragment would report one tool call per token of its arguments. Stage 1 never
                // infers a call from prose (FR-CHAT-014).
                carriedOutput = true;

                if (call.ValueKind == JsonValueKind.Object
                    && call.TryGetProperty("id", out var id)
                    && id.ValueKind == JsonValueKind.String
                    && id.GetString() is { Length: > 0 })
                {
                    toolCallsStarted++;
                }
            }
        }

        /// <summary>
        /// Reads the terminal usage chunk, keeping each component independently optional.
        /// </summary>
        /// <remarks>
        /// Only ever replaces unknown with reported. A runtime that sends usage once and then closes
        /// must not have it erased by a later chunk that omits it (FR-STR-010).
        /// </remarks>
        private void ReadUsage(JsonElement root)
        {
            if (!root.TryGetProperty("usage", out var usage) || usage.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var observed = UsageObservation.Create(
                ReadTokenCount(usage, "prompt_tokens"),
                ReadTokenCount(usage, "completion_tokens"),
                ReadTokenCount(usage, "total_tokens"));

            if (!observed.IsUnknown)
            {
                Usage = observed;
            }
        }

        private static TokenCount? ReadTokenCount(JsonElement usage, string propertyName) =>
            usage.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var tokens)
            && tokens >= 0
                ? TokenCount.FromUpstream(tokens)
                : null;
    }
}
