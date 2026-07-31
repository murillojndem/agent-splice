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

    // `parameters = *( OWS ";" OWS [ parameter ] )` puts optional whitespace before the semicolon.
    [InlineData("text/event-stream ; charset=utf-8")]
    [InlineData("text/event-stream\t;\tcharset=utf-8")]
    [InlineData("text/event-stream; charset=utf-8; boundary=abc")]

    // A quoted parameter value may hold anything qdtext allows, semicolons included.
    [InlineData("""text/event-stream; note="a;b" """)]
    [InlineData("""text/event-stream; note="quoted \" escape" """)]
    public void A_conforming_event_stream_content_type_is_recognised(string contentType)
    {
        Assert.True(Interpreter.MatchesStreamMediaType(contentType));
    }

    [Theory]

    // RFC 9110 writes the parameter itself as optional inside the repetition:
    //   parameters = *( OWS ";" OWS [ parameter ] )
    // so an empty or trailing semicolon is conforming, if sloppy. Rejecting it would refuse a legal
    // sender, which is the class of failure this matcher exists to stop.
    [InlineData("text/event-stream;")]
    [InlineData("text/event-stream; ")]
    [InlineData("text/event-stream;;charset=utf-8")]
    public void An_empty_parameter_is_permitted_by_the_grammar(string contentType)
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

    [Theory]

    // Everything here names the right media type and is still not a media type. Splitting on the
    // first semicolon and ignoring the remainder accepts every one of them, which is the same
    // unchecked claim as whole-string equality, made in the other direction (ADR 0011).
    [InlineData("text/event-stream; ===")]
    [InlineData("text/event-stream; invalid parameter")]
    [InlineData("text/event-stream; charset")]
    [InlineData("text/event-stream; =utf-8")]
    [InlineData("text/event-stream; charset=")]
    [InlineData("text/event-stream; char set=utf-8")]
    [InlineData("text/event-stream; charset =utf-8")]
    [InlineData("text/event-stream; charset= utf-8")]
    [InlineData("""text/event-stream; note="unterminated""")]
    [InlineData("text/event-stream; charset=utf-8; ===")]

    // The media type itself has to be two tokens with nothing between them.
    [InlineData("text / event-stream")]
    [InlineData("text/ event-stream")]
    [InlineData("text /event-stream")]
    [InlineData("/event-stream")]
    [InlineData("text/event-stream/extra")]
    public void A_malformed_content_type_is_not_an_event_stream(string contentType)
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
