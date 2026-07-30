using System.Text;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Measurements;
using AgentSplice.Protocols.OpenAI.ChatCompletions;
using Xunit;

namespace AgentSplice.UnitTests.Protocols;

/// <summary>
/// What one streamed event means in the OpenAI chat completion protocol
/// (docs/SPECIFICATION.md FR-STR-009, FR-STR-010, FR-STR-012, FR-CHAT-014).
/// </summary>
/// <remarks>
/// The interpreter cannot change what a client receives — every byte is already on the wire before a
/// frame reaches it — so nothing here is about output. It is entirely about whether the evidence
/// AgentSplice records describes what actually happened, which is the product.
/// </remarks>
public sealed class OpenAiStreamEventInterpreterTests
{
    [Fact]
    public void The_done_sentinel_is_recognised_as_the_protocol_terminator()
    {
        Assert.True(Interpret("[DONE]").IsProtocolTerminator);
    }

    [Theory]
    [InlineData("""{"choices":[{"delta":{"content":"[DONE]"}}]}""")]
    [InlineData("[DONE] ")]
    [InlineData("prefix [DONE]")]
    public void Only_the_whole_value_is_the_terminator(string data)
    {
        // A model writing those six characters is producing output, not ending a stream. Recognising
        // the sentinel loosely would truncate a legitimate response at the first mention of it
        // (FR-STR-009).
        Assert.False(Interpret(data).IsProtocolTerminator);
    }

    [Fact]
    public void A_role_announcement_is_not_semantic_output()
    {
        // The load-bearing one. Nearly every OpenAI-compatible stream opens with this chunk, so an
        // interpreter that counted it would make time to first token measure time to first chunk for
        // every exchange the product ever records.
        var facts = Interpret("""{"choices":[{"index":0,"delta":{"role":"assistant"}}]}""");

        Assert.False(facts.IsFirstSemanticOutput);
        Assert.False(facts.IsMalformed);
    }

    [Fact]
    public void An_empty_content_delta_is_not_semantic_output()
    {
        Assert.False(Interpret("""{"choices":[{"delta":{"content":""}}]}""").IsFirstSemanticOutput);
    }

    [Fact]
    public void The_first_content_delta_is_the_first_semantic_output()
    {
        var state = New();

        Assert.False(Read(state, """{"choices":[{"delta":{"role":"assistant"}}]}""").IsFirstSemanticOutput);
        Assert.True(Read(state, """{"choices":[{"delta":{"content":"he"}}]}""").IsFirstSemanticOutput);

        // True exactly once. The boundary it drives is single-occurrence in the domain, and a second
        // one would throw from the timeline mid-stream.
        Assert.False(Read(state, """{"choices":[{"delta":{"content":"llo"}}]}""").IsFirstSemanticOutput);
    }

    [Fact]
    public void The_first_tool_call_delta_is_also_semantic_output()
    {
        // A response that calls a tool and says nothing still has a time to first token; without
        // this the boundary would simply be missing for every tool-only exchange.
        var facts = Interpret(
            """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"write"}}]}}]}""");

        Assert.True(facts.IsFirstSemanticOutput);
        Assert.Equal(1, facts.NativeToolCallsStarted);
    }

    [Fact]
    public void A_tool_call_is_counted_once_however_many_fragments_carry_its_arguments()
    {
        // Continuation fragments carry argument text with no id. Counting every fragment would
        // report one tool call per token of its arguments.
        var state = New();

        Read(state, """{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"write","arguments":""}}]}}]}""");
        Read(state, """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"path\":"}}]}}]}""");
        Read(state, """{"choices":[{"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"a\"}"}}]}}]}""");

        Assert.Equal(1, state.Summarise(responseBodyBytes: 100, streamEventCount: 3).NativeToolCallCount);
    }

    [Fact]
    public void Prose_that_merely_looks_like_a_tool_call_produces_none()
    {
        // FR-CHAT-014. Stage 1 never infers a call from text, because a model printing tool syntax is
        // not a model that made one.
        var facts = Interpret(
            """{"choices":[{"delta":{"content":"call write({\"path\":\"a\"}) to save it"}}]}""");

        Assert.Equal(0, facts.NativeToolCallsStarted);
        Assert.True(facts.IsFirstSemanticOutput);
    }

    [Fact]
    public void A_terminal_usage_chunk_is_recorded_with_upstream_provenance()
    {
        // FR-STR-010. The chunk carries an empty choices array, so an interpreter that keyed on
        // choices would discard the one chunk that carries the token counts.
        var state = New();

        Read(state, """{"choices":[{"delta":{"content":"hi"}}]}""");
        Read(state, """{"choices":[],"usage":{"prompt_tokens":41,"completion_tokens":7,"total_tokens":48}}""");

        Assert.Equal(41, state.Usage.PromptTokens?.Value);
        Assert.Equal(MeasurementProvenance.UpstreamReported, state.Usage.WeakestProvenance());
    }

    [Fact]
    public void A_later_chunk_without_usage_does_not_erase_the_usage_already_reported()
    {
        // Some runtimes send usage and then a final terminator chunk. Overwriting on every chunk
        // would leave the exchange claiming the runtime reported nothing.
        var state = New();

        Read(state, """{"choices":[],"usage":{"prompt_tokens":41,"completion_tokens":7}}""");
        Read(state, """{"choices":[{"delta":{},"finish_reason":"stop"}]}""");

        Assert.Equal(41, state.Usage.PromptTokens?.Value);
    }

    [Fact]
    public void A_usage_only_chunk_does_not_shrink_the_recorded_choice_count()
    {
        var state = New();

        Read(state, """{"choices":[{"index":0,"delta":{"content":"hi"}}]}""");
        Read(state, """{"choices":[],"usage":{"prompt_tokens":1}}""");

        Assert.Equal(1, state.Summarise(responseBodyBytes: 50, streamEventCount: 2).ChoiceCount);
    }

    [Fact]
    public void A_payload_that_is_not_json_is_reported_as_malformed_without_throwing()
    {
        // A runtime emitting a broken payload must not be able to fault the gateway relaying it: the
        // bytes have already reached the client either way (NFR 14.2).
        var facts = Interpret("""{"choices":[{"delta":""");

        Assert.True(facts.IsMalformed);
        Assert.False(facts.IsProtocolTerminator);
    }

    [Fact]
    public void A_json_value_that_is_not_an_object_is_malformed()
    {
        Assert.True(Interpret("[1,2,3]").IsMalformed);
    }

    [Fact]
    public void A_deeply_nested_payload_is_reported_as_malformed_rather_than_exhausting_the_stack()
    {
        // Named in docs/THREAT_MODEL.md under "malicious upstream stream". A recursive-descent
        // parser without a depth bound is a stack overflow, which no catch block can contain — the
        // process simply dies, taking every other in-flight exchange with it.
        var nested = new StringBuilder();

        for (var depth = 0; depth < 5_000; depth++)
        {
            nested.Append("{\"a\":");
        }

        nested.Append('1');

        for (var depth = 0; depth < 5_000; depth++)
        {
            nested.Append('}');
        }

        var facts = Interpret(nested.ToString());

        Assert.True(facts.IsMalformed);
    }

    [Fact]
    public void A_second_terminator_after_the_first_changes_nothing()
    {
        // A runtime that repeats its terminator is misbehaving mildly, and the response is already
        // complete. The relay records a single-occurrence boundary from the first one, so a second
        // must not produce anything the timeline would refuse to append twice.
        var state = New();

        Read(state, """{"choices":[{"delta":{"content":"hi"}}]}""");

        Assert.True(Read(state, "[DONE]").IsProtocolTerminator);

        var repeated = Read(state, "[DONE]");

        Assert.True(repeated.IsProtocolTerminator);
        Assert.False(repeated.IsFirstSemanticOutput);
        Assert.False(repeated.IsMalformed);
    }

    [Fact]
    public void An_event_with_no_data_is_interpreted_as_nothing_rather_than_as_malformed()
    {
        // Comments and keepalives are part of the grammar. Calling one a protocol violation would
        // blame a runtime for holding the connection open exactly as the specification tells it to.
        var facts = Interpret(string.Empty);

        Assert.False(facts.IsMalformed);
        Assert.False(facts.IsFirstSemanticOutput);
        Assert.False(facts.IsProtocolTerminator);
    }

    [Fact]
    public void Finish_reasons_accumulate_in_first_seen_order_without_repeating()
    {
        var state = New();

        Read(state, """{"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}""");
        Read(state, """{"choices":[{"index":1,"delta":{},"finish_reason":"stop"}]}""");
        Read(state, """{"choices":[{"index":2,"delta":{},"finish_reason":"stop"}]}""");

        Assert.Equal(
            ["tool_calls", "stop"],
            state.Summarise(responseBodyBytes: 200, streamEventCount: 3).FinishReasons);
    }

    [Fact]
    public void Two_responses_interpreted_at_once_do_not_share_evidence()
    {
        // The interpreter is a singleton and the state is per-response. An implementation that kept
        // mutable fields would let one exchange's usage and tool calls land on another's record
        // under any concurrency at all.
        var interpreter = new OpenAiStreamEventInterpreter();

        var first = interpreter.Begin();
        var second = interpreter.Begin();

        Read(first, """{"choices":[],"usage":{"prompt_tokens":41}}""");

        Assert.Equal(41, first.Usage.PromptTokens?.Value);
        Assert.True(second.Usage.IsUnknown);
    }

    private static IStreamEventInterpreterState New() => new OpenAiStreamEventInterpreter().Begin();

    private static StreamEventFacts Interpret(string data) => Read(New(), data);

    private static StreamEventFacts Read(IStreamEventInterpreterState state, string data) =>
        state.Interpret(ReadOnlySpan<byte>.Empty, Encoding.UTF8.GetBytes(data));
}
