using System.Text;
using AgentSplice.Application.Streaming;
using Xunit;

namespace AgentSplice.UnitTests.Streaming;

/// <summary>
/// SSE framing rules (docs/SPECIFICATION.md FR-STR-004, FR-STR-005, FR-STR-006, FR-STR-008).
/// </summary>
/// <remarks>
/// Feeding a whole stream in one call would prove almost nothing: every interesting failure of an
/// incremental parser happens at a chunk boundary the sender chose and the receiver cannot predict.
/// So the tests here split events at every offset, mid-line, mid-terminator, and mid-character, and
/// assert the same result each time.
/// </remarks>
public sealed class SseFrameReaderTests
{
    private const int Bound = 64 * 1024;

    [Fact]
    public void One_event_per_read_is_recognised()
    {
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: first\n\n"u8));

        Assert.Equal(["first"], Drain(reader));
        Assert.Equal(1, reader.FrameCount);
    }

    [Fact]
    public void Several_events_in_one_read_are_all_recognised()
    {
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: a\n\ndata: b\n\ndata: c\n\n"u8));

        Assert.Equal(["a", "b", "c"], Drain(reader));
    }

    [Fact]
    public void An_event_split_one_byte_at_a_time_is_recognised_exactly_once()
    {
        // The strongest form of FR-STR-004: every possible chunk boundary occurs, including inside
        // the terminator that ends the event.
        using var reader = new SseFrameReader(Bound);

        var wire = "data: hello\n\n"u8.ToArray();
        var events = new List<string>();

        foreach (var single in wire)
        {
            Assert.True(reader.Append([single]));
            events.AddRange(Drain(reader));
        }

        Assert.Equal(["hello"], events);
    }

    [Fact]
    public void A_multi_byte_character_split_across_reads_survives_byte_for_byte()
    {
        // The reader never decodes UTF-8, which is what makes this a non-event rather than a case to
        // handle. If it decoded, a split character would either throw or become a replacement
        // character, and the bytes relayed to the client would no longer be the runtime's bytes.
        using var reader = new SseFrameReader(Bound);

        var wire = Encoding.UTF8.GetBytes("data: café-世界\n\n");

        for (var split = 1; split < wire.Length; split++)
        {
            using var attempt = new SseFrameReader(Bound);

            Assert.True(attempt.Append(wire.AsSpan(0, split)));
            Assert.True(attempt.Append(wire.AsSpan(split)));

            Assert.Equal(["café-世界"], Drain(attempt));
        }
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public void Every_line_ending_the_grammar_allows_terminates_a_line(string ending)
    {
        // All three appear in the wild, and a reader that recognises only LF silently treats a
        // CRLF stream as one enormous unterminated event.
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append(Encoding.UTF8.GetBytes($"data: value{ending}{ending}")));
        reader.EndOfStream();

        Assert.Equal(["value"], Drain(reader));
        Assert.False(reader.TryTakeIncomplete(out _));
    }

    [Fact]
    public void A_stream_ending_on_a_bare_carriage_return_completes_its_last_event()
    {
        // While the stream is open the trailing CR is genuinely ambiguous. At end of stream it is
        // not, and treating it as unresolved anyway would record a malformed event against a runtime
        // whose stream was well formed.
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: value\r\r"u8));
        Assert.Empty(Drain(reader));

        reader.EndOfStream();

        Assert.Equal(["value"], Drain(reader));
        Assert.False(reader.TryTakeIncomplete(out _));
    }

    [Fact]
    public void A_carriage_return_arriving_last_is_not_yet_a_line_ending()
    {
        // Until the next byte arrives there is no way to tell a lone CR from the first half of a
        // CRLF, and guessing splits one event into two at a boundary the runtime never chose.
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: value\r"u8));
        Assert.False(reader.TryReadFrame(out _));

        Assert.True(reader.Append("\n\r\n"u8));
        Assert.Equal(["value"], Drain(reader));
    }

    [Fact]
    public void Multiline_data_is_joined_with_line_feeds()
    {
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: one\ndata: two\ndata: three\n\n"u8));

        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal("one\ntwo\nthree", Text(frame.Data));
        Assert.Equal(3, frame.DataLineCount);
    }

    [Fact]
    public void An_empty_data_line_contributes_an_empty_line_to_the_value()
    {
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: one\ndata:\ndata: three\n\n"u8));

        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal("one\n\nthree", Text(frame.Data));
    }

    [Fact]
    public void A_comment_carries_no_data_and_still_frames_an_event()
    {
        // Keepalives exist to hold a connection open through an idle proxy. They must be relayed and
        // must not be mistaken for output.
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append(": keepalive\n\n"u8));

        Assert.True(reader.TryReadFrame(out var frame));
        Assert.True(frame.IsCommentOnly);
        Assert.Equal(0, frame.DataLineCount);
    }

    [Fact]
    public void A_named_event_reports_its_name()
    {
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("event: ping\ndata: {}\n\n"u8));

        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal("ping", Text(frame.EventName));
        Assert.Equal("{}", Text(frame.Data));
    }

    [Fact]
    public void Exactly_one_space_after_the_colon_belongs_to_the_syntax()
    {
        // Two spaces means the value itself starts with a space. Trimming would quietly alter a
        // payload, which for a JSON body is harmless and for a text payload is not.
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data:  padded\n\n"u8));

        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal(" padded", Text(frame.Data));
    }

    [Fact]
    public void A_line_without_a_colon_is_a_field_with_an_empty_value()
    {
        // The grammar says so. Treating it as malformed would make AgentSplice reject a stream every
        // conforming client accepts.
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data\n\n"u8));

        Assert.True(reader.TryReadFrame(out var frame));
        Assert.Equal(1, frame.DataLineCount);
        Assert.True(frame.Data.IsEmpty);
    }

    [Fact]
    public void An_id_or_retry_field_frames_an_event_and_carries_no_data()
    {
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("id: 7\nretry: 3000\n\n"u8));

        Assert.True(reader.TryReadFrame(out var frame));
        Assert.True(frame.IsCommentOnly);
    }

    [Fact]
    public void The_raw_bytes_of_an_event_are_preserved_exactly()
    {
        // The relay forwards bytes rather than re-encoding them, and this is the property that makes
        // an exact-forwarding assertion meaningful rather than a test of our own round-trip.
        const string Wire = "event: chunk\r\ndata: {\"a\": 1}\r\n\r\n";

        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append(Encoding.UTF8.GetBytes(Wire)));
        Assert.True(reader.TryReadFrame(out var frame));

        Assert.Equal(Wire, Text(frame.Raw));
    }

    [Fact]
    public void Only_the_event_under_assembly_is_retained()
    {
        // FR-STR-008. A reader that kept everything would make every metric it produced cost the
        // size of the response, which is the thing streaming exists to avoid.
        using var reader = new SseFrameReader(Bound);

        for (var i = 0; i < 200; i++)
        {
            Assert.True(reader.Append(Encoding.UTF8.GetBytes($"data: event-{i}\n\n")));
            Assert.Single(Drain(reader));
            Assert.Equal(0, reader.PendingBytes);
        }

        Assert.Equal(200, reader.FrameCount);
    }

    [Fact]
    public void An_event_that_outgrows_its_bound_is_refused_rather_than_assembled()
    {
        using var reader = new SseFrameReader(maxEventBytes: 64);

        Assert.True(reader.Append("data: "u8));
        Assert.False(reader.Append(Encoding.UTF8.GetBytes(new string('x', 128))));
        Assert.False(reader.TryReadFrame(out _));
    }

    [Fact]
    public void A_stream_of_small_events_never_trips_the_bound()
    {
        // The bound is on one event, not on the stream. A reader that accumulated across events
        // would refuse a perfectly ordinary long generation.
        using var reader = new SseFrameReader(maxEventBytes: 64);

        for (var i = 0; i < 500; i++)
        {
            Assert.True(reader.Append("data: small\n\n"u8));
            Assert.Single(Drain(reader));
        }
    }

    [Fact]
    public void An_unterminated_trailing_event_is_reported_as_incomplete()
    {
        // A conforming client discards it, so counting it as delivered would overstate what the
        // client received. Surfacing it is still worthwhile: "the runtime stopped mid-event" is the
        // diagnostic (FR-STR-007).
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: done\n\ndata: half"u8));
        reader.EndOfStream();
        Assert.Single(Drain(reader));

        Assert.True(reader.TryTakeIncomplete(out var frame));
        Assert.False(frame.IsComplete);
        Assert.Equal("half", Text(frame.Data));
    }

    [Fact]
    public void A_stream_that_ended_cleanly_leaves_nothing_incomplete()
    {
        using var reader = new SseFrameReader(Bound);

        Assert.True(reader.Append("data: done\n\n"u8));
        Assert.Single(Drain(reader));

        Assert.False(reader.TryTakeIncomplete(out _));
    }

    private static List<string> Drain(SseFrameReader reader)
    {
        var events = new List<string>();

        while (reader.TryReadFrame(out var frame))
        {
            events.Add(Text(frame.Data));
        }

        return events;
    }

    private static string Text(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
}
