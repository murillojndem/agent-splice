using AgentSplice.Protocols.OpenAI.ChatCompletions;
using Xunit;

namespace AgentSplice.UnitTests.Protocols;

/// <summary>
/// Which content types the OpenAI protocol recognises as one of its own event streams
/// (docs/SPECIFICATION.md FR-STR-001, ADR 0010).
/// </summary>
/// <remarks>
/// The failure this guards against is silent. A conforming runtime answering
/// <c>text/event-stream; charset=utf-8</c> read by whole-string equality takes the buffered path:
/// its bytes still reach the client, so nothing looks broken, and every SSE boundary the product
/// exists to record is simply missing from the trace.
/// </remarks>
public sealed class StreamMediaTypeMatchingTests
{
    private static readonly OpenAiStreamEventInterpreter Interpreter = new();

    [Theory]
    [InlineData("text/event-stream")]
    [InlineData("text/event-stream; charset=utf-8")]
    [InlineData("text/event-stream;charset=UTF-8")]
    [InlineData("Text/Event-Stream")]
    [InlineData("TEXT/EVENT-STREAM; charset=utf-8")]
    [InlineData("  text/event-stream  ")]
    [InlineData("text/event-stream ; charset=utf-8")]
    [InlineData("text/event-stream;")]
    public void A_conforming_event_stream_content_type_is_recognised(string contentType)
    {
        Assert.True(Interpreter.MatchesStreamMediaType(contentType));
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("text/plain")]

    // A prefix is not the media type. Matching by StartsWith would accept this.
    [InlineData("text/event-stream-plus")]
    [InlineData("text/event")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(";")]
    [InlineData("; charset=utf-8")]
    [InlineData("not a media type at all")]
    public void Anything_else_is_not_an_event_stream(string? contentType)
    {
        Assert.False(Interpreter.MatchesStreamMediaType(contentType));
    }

    [Fact]
    public void The_declared_stream_media_type_matches_itself()
    {
        // The value the gateway sends upstream in `Accept` and the value it recognises coming back
        // have to be the same media type, or a runtime that echoes what it was asked for would be
        // misclassified.
        Assert.True(Interpreter.MatchesStreamMediaType(Interpreter.StreamMediaType));
    }
}
