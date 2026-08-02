using System.Text;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Protocols.OpenAI.ChatCompletions;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.UnitTests.Protocols;

/// <summary>
/// Reading a completion request into structure and validation
/// (docs/SPECIFICATION.md FR-CHAT-004, FR-TRACE-003, FR-TRACE-008).
/// </summary>
public sealed class ChatCompletionRequestCodecTests
{
    private const string Minimal = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";

    private readonly OpenAiChatCompletionRequestCodec codec =
        new(Options.Create(new AgentSpliceOptions()));

    [Fact]
    public void A_minimal_request_is_accepted()
    {
        var envelope = Read(Minimal);

        Assert.Equal("m", envelope.Model.Value);
        Assert.False(envelope.StreamRequested);
        Assert.Equal(1, envelope.Summary.MessageCount);
    }

    [Fact]
    public void Roles_are_counted_by_name()
    {
        var envelope = Read("""
            {"model":"m","messages":[
              {"role":"system","content":"s"},
              {"role":"user","content":"u"},
              {"role":"assistant","content":"a"},
              {"role":"user","content":"u2"}]}
            """);

        Assert.Equal(4, envelope.Summary.MessageCount);
        Assert.Equal(1, envelope.Summary.MessageCountsByRole["system"]);
        Assert.Equal(2, envelope.Summary.MessageCountsByRole["user"]);
        Assert.Equal(1, envelope.Summary.MessageCountsByRole["assistant"]);
    }

    [Fact]
    public void A_message_without_a_role_is_counted_as_unspecified()
    {
        var envelope = Read("""{"model":"m","messages":[{"content":"no role here"}]}""");

        Assert.Equal(
            1,
            envelope.Summary.MessageCountsByRole[SafeVocabulary.Unspecified]);
    }

    [Fact]
    public void A_role_the_protocol_does_not_define_is_bucketed_rather_than_recorded()
    {
        // The client picks this string. A cardinality bound stopped the dictionary growing and did
        // nothing about what was in it, which is how a prompt fragment reached the store through
        // "role" while content capture was off.
        var messages = Enumerable.Range(0, 20)
            .Select(index => $$"""{"role":"SENTINEL-PROMPT-{{index}}","content":"x"}""");

        var envelope = Read($$"""{"model":"m","messages":[{{string.Join(",", messages)}}]}""");

        Assert.Equal([SafeVocabulary.Unrecognised], envelope.Summary.MessageCountsByRole.Keys);
        Assert.DoesNotContain(
            "SENTINEL",
            string.Join('|', envelope.Summary.MessageCountsByRole.Keys),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bucketed_roles_still_account_for_every_message()
    {
        const int Count = 20;

        var messages = Enumerable.Range(0, Count)
            .Select(index => $$"""{"role":"role-{{index}}","content":"x"}""");

        var envelope = Read($$"""{"model":"m","messages":[{{string.Join(",", messages)}}]}""");

        Assert.Equal(Count, envelope.Summary.MessageCountsByRole.Values.Sum());
    }

    [Fact]
    public void Unknown_top_level_fields_are_recorded_by_name()
    {
        var envelope = Read(
            """{"model":"m","messages":[{"role":"user"}],"seed":7,"reasoning_effort":"high"}""");

        Assert.Equal(
            [SafeVocabulary.HashName("seed"), SafeVocabulary.HashName("reasoning_effort")],
            envelope.Summary.UnknownTopLevelFieldNames);
    }

    [Fact]
    public void A_modelled_field_is_not_recorded_as_unknown()
    {
        var envelope = Read(
            """{"model":"m","messages":[{"role":"user"}],"temperature":0.5,"max_tokens":10,"top_p":1}""");

        Assert.Empty(envelope.Summary.UnknownTopLevelFieldNames);
    }

    [Fact]
    public void Nothing_is_ever_reported_as_dropped()
    {
        // The empty list is the positive evidence that forwarding was transparent (FR-TRACE-008).
        Assert.Empty(Read(Minimal).Summary.DroppedFieldNames);
    }

    [Fact]
    public void Tools_are_counted_without_reading_their_schemas()
    {
        var envelope = Read("""
            {"model":"m","messages":[{"role":"user"}],"tools":[
              {"type":"function","function":{"name":"a","parameters":{"type":"object"}}},
              {"type":"function","function":{"name":"b"}}]}
            """);

        Assert.Equal(2, envelope.Summary.ToolDeclarationCount);
    }

    [Fact]
    public void Tool_choice_presence_is_recorded_without_its_value()
    {
        Assert.True(Read("""{"model":"m","messages":[{"role":"user"}],"tool_choice":"auto"}""")
            .Summary.ToolChoicePresent);
        Assert.False(Read(Minimal).Summary.ToolChoicePresent);
    }

    [Fact]
    public void A_null_tool_choice_is_not_treated_as_present()
    {
        Assert.False(Read("""{"model":"m","messages":[{"role":"user"}],"tool_choice":null}""")
            .Summary.ToolChoicePresent);
    }

    [Fact]
    public void Stream_options_presence_is_recorded()
    {
        Assert.True(Read("""{"model":"m","messages":[{"role":"user"}],"stream_options":{"include_usage":true}}""")
            .Summary.StreamOptionsPresent);
    }

    [Fact]
    public void The_recorded_body_size_is_the_bytes_received()
    {
        Assert.Equal(Encoding.UTF8.GetByteCount(Minimal), Read(Minimal).Summary.RequestBodyBytes);
    }

    [Fact]
    public void No_message_content_appears_anywhere_in_the_summary()
    {
        const string Sentinel = "SENTINEL-PROMPT-CONTENT";
        var envelope = Read($$"""{"model":"m","messages":[{"role":"user","content":"{{Sentinel}}"}]}""");

        var rendered = string.Join(
            "|",
            envelope.Summary.MessageCountsByRole.Keys
                .Concat(envelope.Summary.UnknownTopLevelFieldNames)
                .Concat(envelope.Summary.DroppedFieldNames));

        Assert.DoesNotContain(Sentinel, rendered, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{\"model\":")]
    public void Malformed_json_is_rejected(string body)
    {
        AssertRejected(body, expectedParam: null);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("\"a string\"")]
    [InlineData("42")]
    public void A_body_that_is_not_an_object_is_rejected(string body)
    {
        AssertRejected(body, expectedParam: null);
    }

    [Theory]
    [InlineData("""{"messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":123,"messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":null,"messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"","messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"   ","messages":[{"role":"user"}]}""")]
    public void An_unusable_model_is_rejected_naming_the_model_field(string body)
    {
        AssertRejected(body, "model");
    }

    [Theory]
    [InlineData("""{"model":"m"}""")]
    [InlineData("""{"model":"m","messages":{}}""")]
    [InlineData("""{"model":"m","messages":[]}""")]
    public void Unusable_messages_are_rejected_naming_the_messages_field(string body)
    {
        AssertRejected(body, "messages");
    }

    [Theory]
    [InlineData("""{"model":"m","messages":[{"role":"user"}],"stream":"true"}""")]
    [InlineData("""{"model":"m","messages":[{"role":"user"}],"stream":1}""")]
    public void A_non_boolean_stream_is_rejected(string body)
    {
        // A truthy non-boolean would be forwarded, the runtime would answer with an event stream,
        // and the non-streaming path would then buffer it.
        AssertRejected(body, "stream");
    }

    [Fact]
    public void An_explicitly_true_stream_is_accepted_and_recorded_as_requested()
    {
        // What the client asked for has to survive into the summary. Everything downstream — which
        // path forwards the request, which media type is asked of the runtime, whether a termination
        // is required — is decided from this one flag.
        var envelope = Read("""{"model":"m","messages":[{"role":"user"}],"stream":true}""");

        Assert.True(envelope.StreamRequested);
        Assert.True(envelope.Summary.StreamRequested);
    }

    [Fact]
    public void An_explicitly_false_stream_is_accepted()
    {
        Assert.False(Read("""{"model":"m","messages":[{"role":"user"}],"stream":false}""").StreamRequested);
    }

    [Theory]
    [InlineData("""{"model":"a","model":"b","messages":[{"role":"user"}]}""", "model")]
    [InlineData("""{"model":"m","messages":[{"role":"user"}],"messages":[]}""", "messages")]
    [InlineData("""{"model":"m","stream":false,"stream":true,"messages":[{"role":"user"}]}""", "stream")]
    public void A_repeated_behavioural_field_is_rejected(string body, string expectedParam)
    {
        // "Last wins" differs between our validation, the splice arithmetic, and the runtime's own
        // parser, so the three could disagree about what was actually sent.
        AssertRejected(body, expectedParam);
    }

    [Fact]
    public void A_repeated_field_that_drives_nothing_is_tolerated()
    {
        Assert.True(codec.Read(Encoding.UTF8.GetBytes(
            """{"model":"m","messages":[{"role":"user"}],"seed":1,"seed":2}""")).Succeeded);
    }

    [Fact]
    public void A_malformed_tools_field_does_not_reject_the_request()
    {
        // Only model and messages are required by the schema; the runtime is the authority on the
        // rest, and refusing here would invent a constraint it does not have.
        var envelope = Read("""{"model":"m","messages":[{"role":"user"}],"tools":"not-an-array"}""");

        Assert.Equal(0, envelope.Summary.ToolDeclarationCount);
    }

    private ChatCompletionEnvelope Read(string body)
    {
        var result = codec.Read(Encoding.UTF8.GetBytes(body));

        Assert.True(result.Succeeded, $"Expected the body to parse: {result.Error?.Message}");

        return result.Envelope!;
    }

    private Application.Errors.GatewayError AssertRejected(string body, string? expectedParam)
    {
        var result = codec.Read(Encoding.UTF8.GetBytes(body));

        Assert.False(result.Succeeded);
        Assert.Equal(400, result.Error!.StatusCode);
        Assert.Equal(Application.Errors.ErrorCodes.InvalidRequest, result.Error.Code);
        Assert.Equal(expectedParam, result.Error.Param);

        return result.Error;
    }
}
