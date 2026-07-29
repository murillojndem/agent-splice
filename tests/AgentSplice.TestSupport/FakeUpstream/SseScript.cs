using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;

namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// Builds byte-exact <c>text/event-stream</c> responses for the fake upstream.
/// </summary>
/// <remarks>
/// The builder works at the byte level rather than the event level. Framing correctness cannot be
/// proven with a fixture that only emits whole, well-formed events: the interesting Stage 1 failures
/// are events split mid-UTF-8-sequence, several events arriving in one read, CRLF line endings, and
/// comment or keepalive lines (docs/TESTING.md SSE fixture family, FR-STR-004 to FR-STR-006).
/// </remarks>
public sealed class SseScript
{
    private readonly List<(string Text, TimeSpan Delay)> segments = [];
    private string lineEnding = "\n";
    private int? splitEveryBytes;
    private TimeSpan splitDelay;
    private TimeSpan headerDelay;
    private TimeSpan trailingDelay;
    private bool closePrematurely;

    private SseScript()
    {
    }

    /// <summary>Starts an empty script that uses LF line endings.</summary>
    public static SseScript Create() => new();

    /// <summary>Switches to CRLF line endings, which the SSE grammar also permits (FR-STR-005).</summary>
    public SseScript UseCrLf()
    {
        lineEnding = "\r\n";
        return this;
    }

    /// <summary>Appends a single-line <c>data:</c> event.</summary>
    public SseScript Data(string data, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(data);
        segments.Add(($"data: {data}{lineEnding}{lineEnding}", delay ?? TimeSpan.Zero));
        return this;
    }

    /// <summary>Appends a multi-line <c>data:</c> event, which the SSE grammar joins with newlines.</summary>
    public SseScript MultilineData(IEnumerable<string> lines, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var builder = new StringBuilder();
        var lineCount = 0;

        foreach (var line in lines)
        {
            builder.Append("data: ").Append(line).Append(lineEnding);
            lineCount++;
        }

        if (lineCount == 0)
        {
            throw new ArgumentException("A multiline data event requires at least one line.", nameof(lines));
        }

        builder.Append(lineEnding);
        segments.Add((builder.ToString(), delay ?? TimeSpan.Zero));
        return this;
    }

    /// <summary>Appends a named event with a <c>data:</c> payload.</summary>
    public SseScript NamedEvent(string eventName, string data, TimeSpan? delay = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(data);

        segments.Add(($"event: {eventName}{lineEnding}data: {data}{lineEnding}{lineEnding}", delay ?? TimeSpan.Zero));
        return this;
    }

    /// <summary>Appends an SSE comment line, the conventional keepalive.</summary>
    public SseScript Comment(string text = "", TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        segments.Add(($": {text}{lineEnding}{lineEnding}", delay ?? TimeSpan.Zero));
        return this;
    }

    /// <summary>Appends a retry directive.</summary>
    public SseScript Retry(TimeSpan interval, TimeSpan? delay = null)
    {
        var milliseconds = (long)interval.TotalMilliseconds;
        segments.Add((
            string.Format(CultureInfo.InvariantCulture, "retry: {0}{1}{1}", milliseconds, lineEnding),
            delay ?? TimeSpan.Zero));
        return this;
    }

    /// <summary>Appends verbatim text, for malformed-framing fixtures.</summary>
    public SseScript Raw(string text, TimeSpan? delay = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        segments.Add((text, delay ?? TimeSpan.Zero));
        return this;
    }

    /// <summary>Appends the OpenAI stream terminator sentinel.</summary>
    public SseScript Done(TimeSpan? delay = null) => Data("[DONE]", delay);

    /// <summary>
    /// Repackages the whole script into fixed-size byte chunks, splitting events and multi-byte
    /// characters at arbitrary positions.
    /// </summary>
    /// <remarks>
    /// Per-segment delays are replaced by <paramref name="delayBetweenChunks"/>, because once the
    /// payload is re-chunked the original event boundaries no longer exist.
    /// </remarks>
    public SseScript SplitEveryBytes(int size, TimeSpan? delayBetweenChunks = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 1);

        splitEveryBytes = size;
        splitDelay = delayBetweenChunks ?? TimeSpan.Zero;
        return this;
    }

    /// <summary>Writes the whole payload one byte at a time.</summary>
    public SseScript SplitByteByByte(TimeSpan? delayBetweenChunks = null) =>
        SplitEveryBytes(1, delayBetweenChunks);

    /// <summary>Delays the response headers, for response-header timeout fixtures.</summary>
    public SseScript WithHeaderDelay(TimeSpan delay)
    {
        headerDelay = delay;
        return this;
    }

    /// <summary>Stalls after the last byte without completing, for idle-stream timeout fixtures.</summary>
    public SseScript WithTrailingDelay(TimeSpan delay)
    {
        trailingDelay = delay;
        return this;
    }

    /// <summary>Resets the connection instead of completing, producing a premature EOF.</summary>
    public SseScript ClosePrematurely()
    {
        closePrematurely = true;
        return this;
    }

    /// <summary>Produces the scripted response.</summary>
    public UpstreamResponseScript Build() => new()
    {
        StatusCode = 200,
        ContentType = "text/event-stream",
        Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["cache-control"] = "no-cache",
        }.AsReadOnly(),
        HeaderDelay = headerDelay,
        TrailingDelay = trailingDelay,
        ClosePrematurely = closePrematurely,
        Chunks = BuildChunks(),
    };

    /// <summary>The exact bytes this script will write, for assertions about forwarded payloads.</summary>
    public byte[] ToBytes()
    {
        var builder = new StringBuilder();

        foreach (var (text, _) in segments)
        {
            builder.Append(text);
        }

        return Encoding.UTF8.GetBytes(builder.ToString());
    }

    private ReadOnlyCollection<UpstreamChunk> BuildChunks()
    {
        if (splitEveryBytes is not { } size)
        {
            var perSegment = new List<UpstreamChunk>(segments.Count);

            foreach (var (text, delay) in segments)
            {
                perSegment.Add(new UpstreamChunk(Encoding.UTF8.GetBytes(text), delay));
            }

            return perSegment.AsReadOnly();
        }

        var payload = ToBytes();
        var chunks = new List<UpstreamChunk>((payload.Length / size) + 1);

        for (var offset = 0; offset < payload.Length; offset += size)
        {
            var length = Math.Min(size, payload.Length - offset);
            chunks.Add(new UpstreamChunk(payload.AsMemory(offset, length), splitDelay));
        }

        return chunks.AsReadOnly();
    }
}
