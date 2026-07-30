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

    private bool sawFirstByte;
    private bool sawFirstFrame;
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
                var at = timeProvider.GetUtcNow();
                var read = await upstream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);

                if (read.BytesRead > 0)
                {
                    if (await ForwardAsync(buffer.AsMemory(0, read.BytesRead), at, cancellationToken)
                            .ConfigureAwait(false) is { } early)
                    {
                        return early;
                    }

                    continue;
                }

                return read.EndOfStream
                    ? EndOfStream(at)
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
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        if (!sawFirstByte)
        {
            sawFirstByte = true;
            recorder.Observe(ObservationType.FirstUpstreamByte, at);
        }

        if (await client.WriteAsync(chunk, cancellationToken).ConfigureAwait(false) is ClientWriteResult.ClientGone)
        {
            return ClientVanished();
        }

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

        DrainFrames(at);

        return null;
    }

    private void DrainFrames(DateTimeOffset at)
    {
        if (reader is null || interpreter is null)
        {
            return;
        }

        while (reader.TryReadFrame(out var frame))
        {
            if (!sawFirstFrame)
            {
                sawFirstFrame = true;

                // Both at the same instant, deliberately. The bytes that completed this event were
                // flushed by the write that preceded the decode, so the moment the client first saw
                // a whole event is the moment those bytes were read — not the later moment
                // AgentSplice finished recognising them.
                recorder.Observe(ObservationType.FirstDecodedEvent, at);
                recorder.Observe(ObservationType.FirstClientEventFlushed, at);
            }

            // A comment is framing, not delivery: a conforming client raises no event for it, so
            // counting keepalives would overstate what the client received.
            if (!frame.IsCommentOnly)
            {
                clientEvents++;
            }

            var facts = interpreter.Interpret(frame.EventName, frame.Data);

            if (facts.IsFirstSemanticOutput && !sawSemanticEvent)
            {
                sawSemanticEvent = true;
                recorder.Observe(ObservationType.FirstSemanticEvent, at);
            }

            if (facts.NativeToolCallsStarted > 0)
            {
                recorder.Observe(ObservationType.NativeToolCallObserved);
            }

            sawMalformedEvent |= facts.IsMalformed;
            sawTerminator |= facts.IsProtocolTerminator;
        }
    }

    private StreamRelayOutcome EndOfStream(DateTimeOffset at)
    {
        if (reader is not null)
        {
            reader.EndOfStream();
            DrainFrames(at);

            if (reader.TryTakeIncomplete(out var partial))
            {
                // A conforming client discards an unterminated trailing event, so it was never
                // delivered. Recording its size is what distinguishes "the runtime stopped
                // mid-event" from "the runtime stopped between events" (FR-STR-007).
                sawMalformedEvent = true;
                incompleteEventBytes = partial.Raw.Length;
            }
        }

        recorder.Observe(ObservationType.UpstreamCompleted, timeProvider.GetUtcNow());

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

    private StreamRelayOutcome Faulted(UpstreamFailure failure)
    {
        if (failure.Reason == UpstreamFailureReason.Cancelled)
        {
            return ClientVanished();
        }

        if (sawTerminator)
        {
            // The runtime already declared the stream complete, so whatever happened to the
            // connection afterwards cost the client nothing. Reporting a failure here would blame a
            // runtime that had finished its work.
            recorder.Observe(ObservationType.UpstreamCompleted, timeProvider.GetUtcNow());

            return Finish(StreamTermination.ProtocolTerminatorReceived, error: null);
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
    /// Not a cancellation if the protocol terminator has already been delivered: a client that hangs
    /// up on a conversation it was told had ended did not cancel anything, and recording it as a
    /// disconnect would make every well-behaved client look like it aborted.
    /// </remarks>
    private StreamRelayOutcome ClientVanished() =>
        sawTerminator
            ? Finish(StreamTermination.ProtocolTerminatorReceived, error: null)
            : Finish(
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
