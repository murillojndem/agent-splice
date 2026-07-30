namespace AgentSplice.Application.Streaming;

/// <summary>
/// One complete server-sent event, as framing alone can describe it.
/// </summary>
/// <remarks>
/// A <c>ref struct</c> over the reader's own buffers, so recognising an event costs no allocation.
/// The spans are valid until the next call that appends to or disposes the reader that produced
/// them; being a <c>ref struct</c>, the compiler already prevents one from outliving that.
///
/// Nothing here interprets the payload. Whether <see cref="Data"/> is JSON, a protocol terminator,
/// or nonsense is a question about a protocol, and answering it at this layer would fuse SSE framing
/// to one protocol's meaning (FR-STR-006).
/// </remarks>
public readonly ref struct SseFrame
{
    internal SseFrame(
        ReadOnlySpan<byte> raw,
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> eventName,
        int dataLineCount,
        bool complete)
    {
        Raw = raw;
        Data = data;
        EventName = eventName;
        DataLineCount = dataLineCount;
        IsComplete = complete;
    }

    /// <summary>The event's bytes exactly as they arrived, including its terminating blank line.</summary>
    public ReadOnlySpan<byte> Raw { get; }

    /// <summary>
    /// The <c>data</c> field values joined with line feeds, per the SSE grammar.
    /// </summary>
    /// <remarks>
    /// Empty both for an event that carried no <c>data</c> field and for one that carried an empty
    /// value. <see cref="DataLineCount"/> distinguishes the two.
    /// </remarks>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>The <c>event</c> field value, or empty when the event was unnamed.</summary>
    public ReadOnlySpan<byte> EventName { get; }

    /// <summary>How many <c>data</c> lines the event carried.</summary>
    public int DataLineCount { get; }

    /// <summary>
    /// False when the stream ended before this event was terminated by a blank line.
    /// </summary>
    /// <remarks>
    /// A conforming client discards an unterminated trailing event, so AgentSplice must not count it
    /// as delivered. It is still surfaced, because "the runtime stopped mid-event" is exactly the
    /// diagnostic worth keeping (FR-STR-007).
    /// </remarks>
    public bool IsComplete { get; }

    /// <summary>True when the event carried no <c>data</c> field at all: a comment or keepalive.</summary>
    public bool IsCommentOnly => DataLineCount == 0;
}
