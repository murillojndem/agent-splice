using System.Text;

namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// Reads an event stream from the client's side, the way a third-party client would.
/// </summary>
/// <remarks>
/// Deliberately an independent implementation. A test that parsed the gateway's output with the
/// gateway's own parser would prove only that AgentSplice agrees with itself, and would keep passing
/// through any framing bug the two share. This one is written straight from the SSE grammar and has
/// no dependency on any production project — the same principle the fake upstream follows.
///
/// Each event carries the moment it was recognised, which is what makes "the client saw event one
/// before the runtime was allowed to send event two" an assertion rather than an assumption.
/// </remarks>
public sealed class SseClientReader(Stream stream, TimeProvider? timeProvider = null) : IDisposable
{
    private static readonly char[] LineBreaks = ['\r', '\n'];

    private readonly Stream stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly List<byte> pending = [];
    private readonly byte[] buffer = new byte[4096];

    private bool endOfStream;

    /// <summary>Every event read so far, in order.</summary>
    public List<SseClientEvent> Events { get; } = [];

    /// <summary>The raw bytes received so far, for byte-for-byte comparison against the fixture.</summary>
    public List<byte> Received { get; } = [];

    /// <summary>
    /// Reads until one more complete event is available, or the stream ends.
    /// </summary>
    /// <returns>The event, or <c>null</c> when the stream ended first.</returns>
    public async Task<SseClientEvent?> ReadEventAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            if (TryTakeEvent() is { } ready)
            {
                Events.Add(ready);
                return ready;
            }

            if (endOfStream)
            {
                return null;
            }

            var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

            if (read == 0)
            {
                endOfStream = true;
                continue;
            }

            pending.AddRange(buffer.AsSpan(0, read).ToArray());
            Received.AddRange(buffer.AsSpan(0, read).ToArray());
        }
    }

    /// <summary>Reads until the stream ends, returning everything it delivered.</summary>
    public async Task<IReadOnlyList<SseClientEvent>> ReadToEndAsync(CancellationToken cancellationToken = default)
    {
        while (await ReadEventAsync(cancellationToken).ConfigureAwait(false) is not null)
        {
        }

        return Events;
    }

    /// <inheritdoc />
    public void Dispose() => stream.Dispose();

    private SseClientEvent? TryTakeEvent()
    {
        var separator = FindEventEnd();

        if (separator < 0)
        {
            return null;
        }

        var raw = pending.GetRange(0, separator);
        pending.RemoveRange(0, separator);

        return Parse(raw, clock.GetUtcNow());
    }

    /// <summary>Finds the end of the first complete event, or -1 when none is complete yet.</summary>
    private int FindEventEnd()
    {
        var lineStart = 0;

        for (var index = 0; index < pending.Count; index++)
        {
            var current = pending[index];
            int terminatorLength;

            if (current == (byte)'\n')
            {
                terminatorLength = 1;
            }
            else if (current == (byte)'\r')
            {
                if (index + 1 == pending.Count)
                {
                    return -1;
                }

                terminatorLength = pending[index + 1] == (byte)'\n' ? 2 : 1;
            }
            else
            {
                continue;
            }

            if (index == lineStart)
            {
                return index + terminatorLength;
            }

            lineStart = index + terminatorLength;
            index = lineStart - 1;
        }

        return -1;
    }

    private static SseClientEvent Parse(List<byte> raw, DateTimeOffset receivedAt)
    {
        var text = Encoding.UTF8.GetString([.. raw]);
        var data = new StringBuilder();
        var dataLines = 0;
        var name = string.Empty;

        foreach (var line in text.Split(LineBreaks, StringSplitOptions.None))
        {
            if (line.Length == 0 || line[0] == ':')
            {
                continue;
            }

            var separator = line.IndexOf(':', StringComparison.Ordinal);
            var field = separator < 0 ? line : line[..separator];
            var value = separator < 0 ? string.Empty : line[(separator + 1)..];

            if (value.StartsWith(' '))
            {
                value = value[1..];
            }

            switch (field)
            {
                case "data":
                    if (dataLines > 0)
                    {
                        data.Append('\n');
                    }

                    data.Append(value);
                    dataLines++;
                    break;

                case "event":
                    name = value;
                    break;

                default:
                    break;
            }
        }

        return new SseClientEvent(name, data.ToString(), dataLines, text, receivedAt);
    }
}

/// <summary>One event as a client received it.</summary>
/// <param name="Name">The <c>event</c> field, empty when the event was unnamed.</param>
/// <param name="Data">The <c>data</c> value, joined across lines.</param>
/// <param name="DataLines">How many <c>data</c> lines the event carried; zero for a comment.</param>
/// <param name="Raw">The event's text exactly as it arrived.</param>
/// <param name="ReceivedAt">When the reader recognised the event.</param>
public sealed record SseClientEvent(
    string Name,
    string Data,
    int DataLines,
    string Raw,
    DateTimeOffset ReceivedAt)
{
    /// <summary>True when the event carried no data: a comment or keepalive.</summary>
    public bool IsCommentOnly => DataLines == 0;
}
