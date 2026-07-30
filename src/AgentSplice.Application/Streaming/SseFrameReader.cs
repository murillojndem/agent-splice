using System.Buffers;

namespace AgentSplice.Application.Streaming;

/// <summary>
/// Recognises complete server-sent events in an incrementally arriving byte stream
/// (docs/SPECIFICATION.md FR-STR-004, FR-STR-005, FR-STR-006, FR-STR-008).
/// </summary>
/// <remarks>
/// Framing only. Nothing here knows what a payload means, what a protocol terminator is, or that the
/// payload is JSON — that separation is the structural form of FR-STR-006, and it is what lets one
/// framing implementation serve any protocol that rides on SSE.
///
/// It is an observer, never a gate: the relay writes bytes to the client before handing them here,
/// so no decoding cost can become a flush delay. It also never decodes UTF-8, which is why a
/// multi-byte character split across two network reads is a non-event by construction rather than a
/// case to handle (FR-STR-004).
///
/// Only the event under assembly is retained. Complete events are handed out as spans over the same
/// buffer and released as soon as the caller moves on, so a stream of any length costs the bound on
/// one event rather than the size of the response (FR-STR-008).
/// </remarks>
public sealed class SseFrameReader : IDisposable
{
    private const byte CarriageReturn = (byte)'\r';
    private const byte LineFeed = (byte)'\n';
    private const byte Colon = (byte)':';
    private const byte Space = (byte)' ';

    private static readonly byte[] DataField = "data"u8.ToArray();
    private static readonly byte[] EventField = "event"u8.ToArray();

    private readonly int maxEventBytes;
    private readonly Queue<int> frameEnds = new();

    private byte[] buffer;
    private byte[]? joinBuffer;
    private int start;
    private int length;
    private int scan;
    private int lineStart;
    private int lastFrameEnd;
    private int frameCount;
    private bool ended;
    private bool disposed;

    /// <summary>Creates a reader bounded to one event's worth of retention.</summary>
    /// <param name="maxEventBytes">
    /// The bound on the event under assembly. This is the only condition under which the streaming
    /// path's memory is not already bounded by the read buffer, so exceeding it stops the relay
    /// rather than degrading it: a bound that kept going would not be a bound.
    /// </param>
    public SseFrameReader(int maxEventBytes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxEventBytes, 0);

        this.maxEventBytes = maxEventBytes;
        buffer = ArrayPool<byte>.Shared.Rent(Math.Min(maxEventBytes, 8 * 1024));
    }

    /// <summary>Bytes held for the event currently being assembled.</summary>
    public int PendingBytes => length - lastFrameEnd;

    /// <summary>Complete events recognised so far.</summary>
    /// <remarks>
    /// Saturates rather than overflowing. A stream long enough to produce more than two billion
    /// events has stopped being counted accurately, and wrapping to a negative count would turn a
    /// large number into a nonsensical one.
    /// </remarks>
    public int FrameCount => frameCount;

    /// <summary>
    /// Appends received bytes, returning <c>false</c> when the event under assembly has outgrown its
    /// bound and the stream must not continue.
    /// </summary>
    public bool Append(ReadOnlySpan<byte> bytes)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (bytes.IsEmpty)
        {
            return true;
        }

        EnsureRoom(bytes.Length);
        bytes.CopyTo(buffer.AsSpan(length));
        length += bytes.Length;

        Scan();

        // Checked on the tail rather than before the copy, because the arriving bytes may be exactly
        // the ones that terminate the event. Refusing beforehand would reject a legitimate event
        // whose final chunk happens to be large.
        return PendingBytes <= maxEventBytes;
    }

    /// <summary>
    /// Declares that no further bytes will arrive, so the last line can be resolved.
    /// </summary>
    /// <remarks>
    /// A carriage return is held back while the stream is open, because a lone CR and the first half
    /// of a CRLF are the same byte. At end of stream the ambiguity is gone, and resolving it here is
    /// what keeps the final event of a CR-terminated stream from being reported as half-received —
    /// which would record a malformed event against a runtime that did nothing wrong.
    ///
    /// Drain <see cref="TryReadFrame"/> after calling this, and only then ask
    /// <see cref="TryTakeIncomplete"/> what was left over.
    /// </remarks>
    public void EndOfStream()
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (ended)
        {
            return;
        }

        ended = true;
        Scan();
    }

    /// <summary>Takes the next complete event, if one has arrived.</summary>
    public bool TryReadFrame(out SseFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (frameEnds.Count == 0)
        {
            frame = default;
            return false;
        }

        var end = frameEnds.Dequeue();
        frame = Materialise(start, end, complete: true);
        start = end;

        return true;
    }

    /// <summary>Takes whatever an ended stream left half-assembled, if anything.</summary>
    public bool TryTakeIncomplete(out SseFrame frame)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        if (frameEnds.Count > 0 || length == start)
        {
            frame = default;
            return false;
        }

        frame = Materialise(start, length, complete: false);
        start = length;

        return true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        // Cleared, because both buffers held decoded model output and a pooled array outlives the
        // response that filled it (docs/SECURITY.md).
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = [];

        if (joinBuffer is { } join)
        {
            ArrayPool<byte>.Shared.Return(join, clearArray: true);
            joinBuffer = null;
        }
    }

    /// <summary>
    /// Walks the unexamined bytes, recording where each complete event ends.
    /// </summary>
    /// <remarks>
    /// Line feed, carriage return, and their pair all end a line; a blank line ends the event. A
    /// carriage return arriving as the last byte is left unexamined, because until the next byte
    /// arrives there is no way to tell a lone CR from the first half of a CRLF — and guessing would
    /// split one event into two at a chunk boundary the runtime never chose (FR-STR-004).
    /// </remarks>
    private void Scan()
    {
        var index = scan;

        while (index < length)
        {
            var current = buffer[index];
            int terminatorLength;

            if (current == LineFeed)
            {
                terminatorLength = 1;
            }
            else if (current == CarriageReturn)
            {
                if (index + 1 == length)
                {
                    if (!ended)
                    {
                        break;
                    }

                    terminatorLength = 1;
                }
                else
                {
                    terminatorLength = buffer[index + 1] == LineFeed ? 2 : 1;
                }
            }
            else
            {
                index++;
                continue;
            }

            if (index == lineStart)
            {
                var end = index + terminatorLength;

                frameEnds.Enqueue(end);
                lastFrameEnd = end;
                lineStart = end;
                index = end;

                if (frameCount < int.MaxValue)
                {
                    frameCount++;
                }
            }
            else
            {
                lineStart = index + terminatorLength;
                index = lineStart;
            }
        }

        scan = index;
    }

    private void EnsureRoom(int incoming)
    {
        if (length + incoming <= buffer.Length)
        {
            return;
        }

        // Everything before `start` has been handed out and consumed, so reclaiming it is free.
        if (start > 0)
        {
            Compact();

            if (length + incoming <= buffer.Length)
            {
                return;
            }
        }

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(buffer.Length * 2, length + incoming));

        buffer.AsSpan(0, length).CopyTo(grown);
        ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
        buffer = grown;
    }

    private void Compact()
    {
        buffer.AsSpan(start, length - start).CopyTo(buffer);

        length -= start;
        scan -= start;
        lineStart -= start;
        lastFrameEnd = Math.Max(lastFrameEnd - start, 0);

        var pending = frameEnds.Count;

        for (var i = 0; i < pending; i++)
        {
            frameEnds.Enqueue(frameEnds.Dequeue() - start);
        }

        start = 0;
    }

    /// <summary>
    /// Reads the fields of one event out of the bytes that make it up.
    /// </summary>
    /// <remarks>
    /// The SSE grammar is deliberately permissive and this follows it rather than tightening it: a
    /// line with no colon is a field with an empty value, an unknown field name is ignored, and
    /// exactly one space after the colon is part of the syntax rather than the value. Tightening any
    /// of these would make AgentSplice reject streams that every conforming client accepts.
    ///
    /// A single-line <c>data</c> value — the overwhelmingly common case — is handed out as a span
    /// into the frame itself. Only a multi-line value needs the join buffer the grammar requires.
    /// </remarks>
    private SseFrame Materialise(int from, int to, bool complete)
    {
        var frame = buffer.AsSpan(from, to - from);

        var dataLines = 0;
        var dataStart = 0;
        var dataLength = 0;
        var eventStart = 0;
        var eventLength = 0;
        var joined = 0;

        var position = 0;

        while (position < frame.Length)
        {
            var lineEnd = position;

            while (lineEnd < frame.Length
                && frame[lineEnd] != LineFeed
                && frame[lineEnd] != CarriageReturn)
            {
                lineEnd++;
            }

            var line = frame[position..lineEnd];
            var next = lineEnd;

            if (next < frame.Length)
            {
                next += frame[next] == CarriageReturn && next + 1 < frame.Length && frame[next + 1] == LineFeed
                    ? 2
                    : 1;
            }

            if (line.Length == 0)
            {
                position = next;
                continue;
            }

            if (line[0] == Colon)
            {
                position = next;
                continue;
            }

            var separator = line.IndexOf(Colon);
            var name = separator < 0 ? line : line[..separator];

            var valueStart = position + (separator < 0 ? line.Length : separator + 1);
            var valueLength = separator < 0 ? 0 : line.Length - separator - 1;

            if (valueLength > 0 && frame[valueStart] == Space)
            {
                valueStart++;
                valueLength--;
            }

            if (name.SequenceEqual(DataField))
            {
                dataLines++;

                if (dataLines == 1)
                {
                    dataStart = valueStart;
                    dataLength = valueLength;
                }
                else
                {
                    joined = Join(frame, joined, dataLines, dataStart, dataLength, valueStart, valueLength);
                }
            }
            else if (name.SequenceEqual(EventField))
            {
                // Last one wins, per the grammar's "set the event type buffer".
                eventStart = valueStart;
                eventLength = valueLength;
            }

            position = next;
        }

        var data = dataLines > 1
            ? joinBuffer.AsSpan(0, joined)
            : frame.Slice(dataStart, dataLength);

        return new SseFrame(
            frame,
            data,
            frame.Slice(eventStart, eventLength),
            dataLines,
            complete);
    }

    private int Join(
        ReadOnlySpan<byte> frame,
        int joined,
        int dataLines,
        int firstStart,
        int firstLength,
        int valueStart,
        int valueLength)
    {
        joinBuffer ??= ArrayPool<byte>.Shared.Rent(Math.Min(maxEventBytes, 8 * 1024));

        if (dataLines == 2)
        {
            EnsureJoinRoom(firstLength);
            frame.Slice(firstStart, firstLength).CopyTo(joinBuffer.AsSpan());
            joined = firstLength;
        }

        EnsureJoinRoom(joined + valueLength + 1);

        joinBuffer[joined++] = LineFeed;
        frame.Slice(valueStart, valueLength).CopyTo(joinBuffer.AsSpan(joined));

        return joined + valueLength;
    }

    private void EnsureJoinRoom(int required)
    {
        if (joinBuffer!.Length >= required)
        {
            return;
        }

        var grown = ArrayPool<byte>.Shared.Rent(Math.Max(joinBuffer.Length * 2, required));

        joinBuffer.AsSpan().CopyTo(grown);
        ArrayPool<byte>.Shared.Return(joinBuffer, clearArray: true);
        joinBuffer = grown;
    }
}
