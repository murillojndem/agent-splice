using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// Reads meaning out of the events of one streamed response, without altering any of them.
/// </summary>
/// <remarks>
/// Framing and meaning are separated on purpose (FR-STR-006). <c>SseFrameReader</c> knows where an
/// event begins and ends and nothing else; this knows that <c>[DONE]</c> ends an OpenAI stream, that
/// a first chunk announcing a role is not yet output, and that a trailing chunk with no choices may
/// still carry usage. Fusing the two would tie SSE framing to one protocol's vocabulary and make the
/// next protocol a rewrite rather than an implementation.
///
/// Nothing here can change what the client receives. The bytes have already been written by the time
/// a frame arrives here, which is the property that makes interpretation safe to get wrong: a
/// misread event costs evidence, never output.
/// </remarks>
public interface IStreamEventInterpreter
{
    /// <summary>The media type this protocol's streamed responses use.</summary>
    string StreamMediaType { get; }

    /// <summary>
    /// True when a runtime's <c>Content-Type</c> names this protocol's streamed media type.
    /// </summary>
    /// <remarks>
    /// A question for the protocol rather than for the relay. A media type is case-insensitive and
    /// may carry parameters, so the answer is not string equality, and the rules belong wherever the
    /// media type itself is defined. One implementation means one answer: the relay and the
    /// orchestrator cannot drift into classifying the same response two ways.
    ///
    /// Classification only. What reaches the client is the runtime's own header value, unchanged.
    /// </remarks>
    /// <param name="contentType">The header value as the runtime sent it, or <c>null</c>.</param>
    bool MatchesStreamMediaType(string? contentType);

    /// <summary>Opens interpretation state for one response.</summary>
    /// <remarks>
    /// The interpreter is shared and must stay stateless; the state is per-response. An
    /// implementation that kept mutable fields would let one exchange's events count towards
    /// another's evidence under any concurrency at all.
    /// </remarks>
    IStreamEventInterpreterState Begin();
}

/// <summary>Interpretation state for a single streamed response.</summary>
public interface IStreamEventInterpreterState
{
    /// <summary>Usage as the runtime reported it, or unknown when it reported none.</summary>
    UsageObservation Usage { get; }

    /// <summary>Reads one event and reports what it means.</summary>
    /// <param name="eventName">The SSE <c>event</c> field, empty when the event was unnamed.</param>
    /// <param name="data">The SSE <c>data</c> value, already joined across lines.</param>
    StreamEventFacts Interpret(ReadOnlySpan<byte> eventName, ReadOnlySpan<byte> data);

    /// <summary>Produces the structural summary the observed events support.</summary>
    /// <param name="responseBodyBytes">Bytes forwarded to the client.</param>
    /// <param name="streamEventCount">Events delivered to the client.</param>
    StructuralResponseSummary Summarise(int responseBodyBytes, int streamEventCount);
}

/// <summary>What one streamed event turned out to be.</summary>
/// <remarks>
/// <see cref="IsFirstSemanticOutput"/> is deliberately not "this event carried output". It is true
/// exactly once per response, on the first event that carried any, because the boundary it drives is
/// time to first token — and an OpenAI-compatible runtime's first chunk usually announces a role and
/// no text at all. Treating that chunk as output would report a time-to-first-token that measures
/// something else entirely, which is the same class of error as labelling prompt throughput as
/// generation throughput.
/// </remarks>
public readonly record struct StreamEventFacts
{
    /// <summary>True when this event is the protocol's end-of-stream sentinel.</summary>
    public bool IsProtocolTerminator { get; init; }

    /// <summary>True on the one event that first carried model output.</summary>
    public bool IsFirstSemanticOutput { get; init; }

    /// <summary>True when the payload could not be interpreted as this protocol's event.</summary>
    public bool IsMalformed { get; init; }

    /// <summary>Structured tool calls this event started.</summary>
    public int NativeToolCallsStarted { get; init; }
}
