using System.Buffers;
using System.Globalization;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using AgentSplice.Application.Runtimes;
using AgentSplice.Application.Streaming;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// One relayed response: the read/write loop and everything it observed.
/// </summary>
/// <remarks>
/// A per-response object rather than a method with a dozen locals, because the loop and the way it
/// ended have to agree about the same facts — how many events reached the client, whether the
/// protocol said it was finished, whether anything was malformed — and threading those through
/// closures is how the two drift apart.
/// </remarks>
internal sealed class StreamRelayPump
{
    internal const string FallbackMediaType = "application/json";

    private const int ReadBufferBytes = 16 * 1024;

    private readonly ExchangeRecorder recorder;
    private readonly IUpstreamResponseBody upstream;
    private readonly IClientResponseSink client;
    private readonly IStreamEventInterpreterState? interpreter;
    private readonly IChatCompletionResponseCodec responseCodec;
    private readonly LimitsOptions limits;
    private readonly TimeProvider timeProvider;
    private readonly UpstreamResponseMetadata metadata;
    private readonly string mediaType;
    private readonly bool streamed;

    private readonly SseFrameReader? reader;
    private readonly BufferedEvidence? evidence;

    private long clientBytes;
    private int clientEvents;
    private int incompleteEventBytes;

    /// <summary>When the write carrying the most recent bytes finished flushing to the client.</summary>
    /// <remarks>
    /// Carried forward rather than passed down, because the frame a flush completed may only be
    /// recognised in a later drain — at end of stream, the bytes that finish the last event were
    /// flushed by a write that has already returned.
    /// </remarks>
    private DateTimeOffset? lastFlushCompletedAt;

    /// <summary>When the protocol's end-of-stream sentinel was recognised.</summary>
    private DateTimeOffset terminatorAt;

    // Four separate boundaries, so four separate flags. One shared "saw the first frame" made the
    // first decode, the first client event, and the first semantic event a single fact recorded at a
    // single instant, which is precisely what a timeline exists to keep apart (ADR 0010).
    private bool sawFirstByte;
    private bool sawDecodedFrame;
    private bool sawClientEvent;
    private bool sawSemanticEvent;
    private bool sawTerminator;
    private bool sawMalformedEvent;

    internal StreamRelayPump(
        ExchangeRecorder recorder,
        IUpstreamResponseBody upstream,
        IClientResponseSink client,
        IStreamEventInterpreterState? interpreter,
        IChatCompletionResponseCodec responseCodec,
        LimitsOptions limits,
        TimeProvider timeProvider,
        UpstreamResponseMetadata metadata,
        string mediaType,
        bool streamed)
    {
        this.recorder = recorder;
        this.upstream = upstream;
        this.client = client;
        this.interpreter = interpreter;
        this.responseCodec = responseCodec;
        this.limits = limits;
        this.timeProvider = timeProvider;
        this.metadata = metadata;
        this.mediaType = mediaType;
        this.streamed = streamed;

        reader = streamed ? new SseFrameReader(limits.MaxStreamEventBytes) : null;
        evidence = streamed ? null : new BufferedEvidence(limits.MaxUpstreamCompletionBodyBytes);
    }

    internal async Task<StreamRelayOutcome> RunAsync(CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBufferBytes);

        try
        {
            while (true)
            {
                var read = await upstream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (read.BytesRead > 0)
                {
                    // Taken after the read returns, never before it. A clock read before an await is
                    // a claim about when AgentSplice began waiting, and a runtime that thinks for
                    // twenty seconds would have its first byte dated twenty seconds early
                    // (ADR 0010).
                    var bytesReceivedAt = timeProvider.GetUtcNow();

                    if (!sawFirstByte)
                    {
                        sawFirstByte = true;
                        recorder.Observe(ObservationType.FirstUpstreamByte, bytesReceivedAt);
                    }

                    if (await ForwardAsync(buffer.AsMemory(0, read.BytesRead), cancellationToken)
                            .ConfigureAwait(false) is { } early)
                    {
                        return early;
                    }

                    if (sawTerminator)
                    {
                        // The protocol said the response was finished, so there is nothing left to
                        // wait for. Reading on would hold the client open until EOF or an idle
                        // budget, and would date completion at whichever arrived first.
                        return ProtocolCompleted();
                    }

                    continue;
                }

                return read.EndOfStream
                    ? EndOfStream()
                    : Faulted(read.Failure!);
            }
        }
        finally
        {
            // Cleared on return. The buffer held model output, and a pooled array outlives the
            // exchange that filled it: the next renter sees whatever was left there, and the classic
            // way content escapes is a caller that trusts the array's length instead of its read
            // count. This runs once per exchange rather than once per read, so it costs one memset
            // against a stream that ran for seconds.
            ArrayPool<byte>.Shared.Return(buffer, clearArray: true);
            reader?.Dispose();
        }
    }

    /// <summary>
    /// Writes one chunk to the client and then observes it, returning an outcome only if the relay
    /// cannot continue.
    /// </summary>
    private async Task<StreamRelayOutcome?> ForwardAsync(
        ReadOnlyMemory<byte> chunk,
        CancellationToken cancellationToken)
    {
        if (await client.WriteAsync(chunk, cancellationToken).ConfigureAwait(false) is ClientWriteResult.ClientGone)
        {
            return ClientVanished();
        }

        // The write has flushed, so these bytes are the client's as of now. Held rather than
        // recorded: bytes are not an event, and the boundary this timestamp belongs to is the first
        // complete non-comment event they made available.
        lastFlushCompletedAt = timeProvider.GetUtcNow();

        clientBytes += chunk.Length;
        evidence?.Append(chunk.Span);

        if (reader is null)
        {
            return null;
        }

        if (!reader.Append(chunk.Span))
        {
            // AgentSplice's own bound, not the runtime's misbehaviour. The client is abandoned
            // rather than closed politely, because an event stream that stops early but ends
            // cleanly at the HTTP level is indistinguishable from a complete one.
            client.Abort();

            return Finish(
                StreamTermination.LimitExceeded,
                GatewayErrorCatalogue.For(FailureClass.InvalidUpstreamStream),
                aborted: true);
        }

        DrainFrames();

        return null;
    }

    /// <summary>
    /// Reads out every event the arrived bytes completed, recording each boundary at the operation
    /// that produced it.
    /// </summary>
    /// <remarks>
    /// Three of the four streaming boundaries are decided here, and they are three different
    /// instants: the client's flush finished before AgentSplice looked at the bytes, decoding
    /// happened when a frame turned out to be complete, and semantic classification happened after
    /// the protocol read the payload. Collapsing them onto one timestamp — which is what a single
    /// pre-read clock reading did — makes time to first token, flush latency, and decode cost
    /// indistinguishable from each other and from zero (ADR 0010).
    ///
    /// Boundaries are appended in the order they occurred rather than the order they were learned.
    /// The client-event boundary is stamped with a flush that has already returned, so it precedes
    /// everything else this drain observes and is appended first; a keepalive completing before the
    /// first data event in the same read would otherwise put the timeline out of order.
    /// </remarks>
    private void DrainFrames()
    {
        if (reader is null || interpreter is null)
        {
            return;
        }

        DateTimeOffset? clientEventAt = null;
        List<(ObservationType Type, DateTimeOffset At)>? decoded = null;

        while (reader.TryReadFrame(out var frame))
        {
            var frameDecodedAt = timeProvider.GetUtcNow();

            if (!sawDecodedFrame)
            {
                sawDecodedFrame = true;
                Defer(ref decoded, ObservationType.FirstDecodedEvent, frameDecodedAt);
            }

            // A comment is framing, not delivery: a conforming client raises no event for it, so
            // counting keepalives would overstate what the client received — and dating the first
            // client event from a keepalive would report a response as having reached the client
            // before it carried anything.
            if (!frame.IsCommentOnly)
            {
                clientEvents++;

                if (!sawClientEvent && lastFlushCompletedAt is { } flushedAt)
                {
                    sawClientEvent = true;
                    clientEventAt = flushedAt;
                }
            }

            var facts = interpreter.Interpret(frame.EventName, frame.Data);

            if (facts.IsFirstSemanticOutput && !sawSemanticEvent)
            {
                sawSemanticEvent = true;
                Defer(ref decoded, ObservationType.FirstSemanticEvent, timeProvider.GetUtcNow());
            }

            if (facts.NativeToolCallsStarted > 0)
            {
                Defer(ref decoded, ObservationType.NativeToolCallObserved, timeProvider.GetUtcNow());
            }

            sawMalformedEvent |= facts.IsMalformed;

            if (facts.IsProtocolTerminator)
            {
                sawTerminator = true;
                terminatorAt = timeProvider.GetUtcNow();

                // Nothing after the protocol's own end-of-stream belongs to this response. Bytes the
                // runtime coalesced behind its terminator have already been forwarded and cannot be
                // recalled, but interpreting them would extend a response the protocol declared
                // finished.
                break;
            }
        }

        if (clientEventAt is { } clientEvent)
        {
            recorder.Observe(ObservationType.FirstClientEventFlushed, clientEvent);
        }

        if (decoded is null)
        {
            return;
        }

        foreach (var (type, at) in decoded)
        {
            recorder.Observe(type, at);
        }
    }

    private static void Defer(
        ref List<(ObservationType Type, DateTimeOffset At)>? observations,
        ObservationType type,
        DateTimeOffset at)
    {
        observations ??= [];
        observations.Add((type, at));
    }

    /// <summary>The protocol declared the response complete, so the relay stops without reading on.</summary>
    /// <remarks>
    /// Completion is dated from recognising the terminator rather than from the transport ending.
    /// A runtime that sends <c>[DONE]</c> and holds the connection open would otherwise stretch the
    /// upstream duration, and the generation window derived from it, over a stall that produced
    /// nothing (ADR 0010, superseding ADR 0009's claim that this costs latency but not accuracy).
    /// </remarks>
    private StreamRelayOutcome ProtocolCompleted()
    {
        recorder.Observe(ObservationType.UpstreamCompleted, terminatorAt);

        // The anomaly still outranks the tidy ending: a stream that carried a malformed event and
        // then terminated properly is more usefully described by the first fact.
        return Finish(
            sawMalformedEvent ? StreamTermination.MalformedEvent : StreamTermination.ProtocolTerminatorReceived,
            error: null);
    }

    private StreamRelayOutcome EndOfStream()
    {
        if (reader is not null)
        {
            reader.EndOfStream();
            DrainFrames();

            if (reader.TryTakeIncomplete(out var partial))
            {
                // A conforming client discards an unterminated trailing event, so it was never
                // delivered. Recording its size is what distinguishes "the runtime stopped
                // mid-event" from "the runtime stopped between events" (FR-STR-007).
                sawMalformedEvent = true;
                incompleteEventBytes = partial.Raw.Length;
            }
        }

        // Dated from the terminator when the final drain found one, so the two ways a protocol-
        // terminated stream can end — the sentinel mid-read, and the sentinel completed only by end
        // of stream — agree about when the response finished.
        recorder.Observe(
            ObservationType.UpstreamCompleted,
            sawTerminator ? terminatorAt : timeProvider.GetUtcNow());

        if (!streamed)
        {
            return Finish(StreamTermination.NotApplicable, error: null);
        }

        // The anomaly outranks the tidy ending. A stream that both misbehaved and terminated
        // properly is more usefully described by the first fact; the second survives in the
        // completion details rather than being lost.
        var termination = sawMalformedEvent
            ? StreamTermination.MalformedEvent
            : sawTerminator
                ? StreamTermination.ProtocolTerminatorReceived
                : StreamTermination.NormalCompletion;

        return Finish(termination, error: null);
    }

    /// <summary>
    /// The stream ended for a reason that is not a clean close.
    /// </summary>
    /// <remarks>
    /// Unreachable after the protocol terminator, because recognising it ends the loop before
    /// another read is issued. That is the point: a timeout or a reset that arrives after the
    /// runtime has already said it finished can no longer be reported against it (ADR 0010).
    /// </remarks>
    private StreamRelayOutcome Faulted(UpstreamFailure failure)
    {
        if (failure.Reason == UpstreamFailureReason.Cancelled)
        {
            return ClientVanished();
        }

        if (failure.Phase is { } phase)
        {
            recorder.Observe(
                ObservationType.TimeoutFired,
                SafeDetails.Create("timeout.phase", phase.ToString()));
        }

        if (streamed)
        {
            client.Abort();
        }

        return Finish(
            streamed
                ? failure.Reason == UpstreamFailureReason.Timeout
                    ? StreamTermination.Timeout
                    : StreamTermination.ConnectionLost
                : StreamTermination.NotApplicable,
            GatewayErrorCatalogue.Translate(failure),
            aborted: streamed);
    }

    /// <summary>
    /// The client stopped listening.
    /// </summary>
    /// <remarks>
    /// Reached only before the protocol terminator. A client that hangs up on a conversation it was
    /// told had ended is not cancelling anything, and it can no longer reach here: recognising the
    /// terminator ends the relay, so there is no later write for the client to be absent from
    /// (ADR 0010).
    /// </remarks>
    private StreamRelayOutcome ClientVanished() =>
        Finish(
            streamed ? StreamTermination.ClientCancelled : StreamTermination.NotApplicable,
            error: null,
            clientGone: true);

    private StreamRelayOutcome Finish(
        StreamTermination termination,
        GatewayError? error,
        bool clientGone = false,
        bool aborted = false)
    {
        var summary = Summarise();

        return new StreamRelayOutcome
        {
            Termination = termination,
            Error = error,
            StatusCode = metadata.StatusCode,
            MediaType = mediaType,
            ClientBytes = clientBytes,
            ClientEvents = clientEvents,
            IncompleteEventBytes = incompleteEventBytes,
            ProtocolTerminatorObserved = sawTerminator,
            Summary = summary,
            Usage = interpreter?.Usage ?? evidence?.Usage ?? UsageObservation.Unknown,
            ClientGone = clientGone,
            Aborted = aborted,
            Streamed = streamed,
        };
    }

    private StructuralResponseSummary? Summarise()
    {
        if (interpreter is { } state)
        {
            return state.Summarise(Truncate(clientBytes), clientEvents);
        }

        return evidence?.Summarise(responseCodec);
    }

    private static int Truncate(long bytes) => bytes > int.MaxValue ? int.MaxValue : (int)bytes;

    /// <summary>
    /// Collects a non-streamed answer so it can be summarised the way the buffered path summarises
    /// one.
    /// </summary>
    /// <remarks>
    /// Only reached when a runtime answers a streaming request with an ordinary body. The bytes have
    /// already been relayed, so this costs the client nothing; it exists so that asking to stream
    /// never yields less evidence than not asking. Bounded by the same limit the buffered path uses,
    /// and beyond it the summary is absent rather than partial — half a body would produce a
    /// structural claim about a response nobody saw in full.
    /// </remarks>
    private sealed class BufferedEvidence(long maxBytes)
    {
        private readonly ArrayBufferWriter<byte> buffer = new();
        private bool exceeded;

        public UsageObservation? Usage { get; private set; }

        public void Append(ReadOnlySpan<byte> bytes)
        {
            if (exceeded)
            {
                return;
            }

            if (buffer.WrittenCount + bytes.Length > maxBytes)
            {
                exceeded = true;
                buffer.Clear();
                return;
            }

            buffer.Write(bytes);
        }

        public StructuralResponseSummary? Summarise(IChatCompletionResponseCodec codec)
        {
            if (exceeded)
            {
                return null;
            }

            var facts = codec.Read(buffer.WrittenSpan, FallbackMediaType);

            Usage = facts.Usage;

            return facts.Summary;
        }
    }
}
