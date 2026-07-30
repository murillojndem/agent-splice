using System.Text;
using AgentSplice.Domain.Measurements;
using AgentSplice.Protocols.OpenAI.ChatCompletions;
using Xunit;

namespace AgentSplice.UnitTests.Protocols;

/// <summary>
/// Structural evidence from a completion response
/// (docs/SPECIFICATION.md FR-OBS-003, FR-CHAT-014, FR-CHAT-015).
/// </summary>
public sealed class ChatCompletionResponseCodecTests
{
    private const string Simple = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":41,"completion_tokens":7,"total_tokens":48}}
        """;

    private readonly OpenAiChatCompletionResponseCodec codec = new();

    [Fact]
    public void Choices_and_finish_reasons_are_summarised()
    {
        var facts = Read(Simple);

        Assert.Equal(1, facts.Summary?.ChoiceCount);
        Assert.Equal(["stop"], facts.Summary?.FinishReasons);
    }

    [Fact]
    public void Finish_reasons_are_deduplicated_in_first_seen_order()
    {
        var facts = Read("""
            {"choices":[
              {"finish_reason":"length"},
              {"finish_reason":"stop"},
              {"finish_reason":"length"}]}
            """);

        Assert.Equal(["length", "stop"], facts.Summary?.FinishReasons);
    }

    [Fact]
    public void Usage_is_read_with_upstream_provenance()
    {
        var facts = Read(Simple);

        Assert.Equal(41, facts.Usage.PromptTokens?.Value);
        Assert.Equal(7, facts.Usage.CompletionTokens?.Value);
        Assert.Equal(MeasurementProvenance.UpstreamReported, facts.Usage.WeakestProvenance());
        Assert.True(facts.Summary?.UsageReported);
    }

    [Fact]
    public void Absent_usage_stays_unknown_rather_than_becoming_zero()
    {
        // Zero is a claim that no tokens were consumed. Absence is a claim that we do not know.
        var facts = Read("""{"choices":[{"finish_reason":"stop"}]}""");

        Assert.True(facts.Usage.IsUnknown);
        Assert.Null(facts.Usage.PromptTokens);
        Assert.False(facts.Summary?.UsageReported);
    }

    [Fact]
    public void A_partially_reported_usage_keeps_each_component_independent()
    {
        var facts = Read("""{"choices":[],"usage":{"prompt_tokens":10}}""");

        Assert.Equal(10, facts.Usage.PromptTokens?.Value);
        Assert.Null(facts.Usage.CompletionTokens);
        Assert.Null(facts.Usage.TotalTokens);
    }

    [Fact]
    public void A_reported_total_is_read_rather_than_recomputed()
    {
        // A runtime may count tokens AgentSplice cannot see, so its total is evidence in its own
        // right rather than a checksum of the two components.
        var facts = Read("""{"choices":[],"usage":{"prompt_tokens":1,"completion_tokens":1,"total_tokens":99}}""");

        Assert.Equal(99, facts.Usage.TotalTokens?.Value);
    }

    [Fact]
    public void Native_tool_calls_are_counted_as_protocol_data()
    {
        var facts = Read("""
            {"choices":[{"message":{"role":"assistant","tool_calls":[
              {"id":"c1","type":"function","function":{"name":"a","arguments":"{}"}},
              {"id":"c2","type":"function","function":{"name":"b","arguments":"{}"}}]}}]}
            """);

        Assert.Equal(2, facts.Summary?.NativeToolCallCount);
    }

    [Fact]
    public void Prose_that_merely_looks_like_a_tool_call_is_not_counted()
    {
        // A model printing tool syntax is not a model that made a tool call (FR-CHAT-014).
        var facts = Read("""
            {"choices":[{"message":{"role":"assistant",
              "content":"You could call {\"tool_calls\":[{\"function\":{\"name\":\"rm\"}}]} here."},
              "finish_reason":"stop"}]}
            """);

        Assert.Equal(0, facts.Summary?.NativeToolCallCount);
    }

    [Fact]
    public void A_non_streamed_response_reports_no_stream_events()
    {
        Assert.Equal(0, Read(Simple).Summary?.StreamEventCount);
    }

    [Fact]
    public void The_recorded_body_size_is_the_bytes_received()
    {
        Assert.Equal(Encoding.UTF8.GetByteCount(Simple), Read(Simple).Summary?.ResponseBodyBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"id\": \"chatcmpl-1\", \"choices\": [ {\"index\": 0, ")]
    public void An_uninterpretable_body_yields_no_summary_rather_than_an_error(string body)
    {
        // Reading is for evidence only and never gates forwarding: an unreadable body costs a
        // structural summary and nothing else.
        var facts = Read(body);

        Assert.False(facts.WasInterpretable);
        Assert.Null(facts.Summary);
        Assert.True(facts.Usage.IsUnknown);
    }

    [Fact]
    public void A_body_with_a_non_json_media_type_is_still_read_when_it_parses()
    {
        // The media type is a hint, not a gate. A runtime mislabelling valid JSON should still
        // yield evidence.
        var facts = codec.Read(Encoding.UTF8.GetBytes(Simple), "text/plain");

        Assert.True(facts.WasInterpretable);
    }

    [Fact]
    public void A_response_without_choices_is_summarised_as_zero_choices()
    {
        var facts = Read("""{"id":"chatcmpl-1","object":"chat.completion"}""");

        Assert.True(facts.WasInterpretable);
        Assert.Equal(0, facts.Summary?.ChoiceCount);
    }

    [Fact]
    public void A_negative_token_count_is_ignored_rather_than_recorded()
    {
        var facts = Read("""{"choices":[],"usage":{"prompt_tokens":-5,"completion_tokens":3}}""");

        Assert.Null(facts.Usage.PromptTokens);
        Assert.Equal(3, facts.Usage.CompletionTokens?.Value);
    }

    private Application.Protocols.ChatCompletionResponseFacts Read(string body) =>
        codec.Read(Encoding.UTF8.GetBytes(body), "application/json");
}
