using System.Text;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Observations;
using AgentSplice.Protocols.OpenAI.ChatCompletions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Exchanges;

/// <summary>
/// Which operation each streaming boundary is timestamped at, and when the relay stops reading
/// (ADR 0010, docs/SPECIFICATION.md FR-TRACE-006, FR-STR-011, FR-STR-012).
/// </summary>
/// <remarks>
/// Driven through <see cref="ChatCompletionStreamRelay"/> with a scripted byte source and a
/// controllable sink, because the claims under test are about <em>when</em> a clock was read, and
/// nothing that runs against a real socket can pin that down. Both fakes advance the clock inside
/// the operation they model, so a boundary stamped before the await and one stamped after it are
/// separated by a full second rather than by microseconds — a tolerance no accidental pass can
/// squeeze through.
///
/// The clock also auto-advances on every read, so two boundaries can never share a timestamp by
/// coincidence. A pump that collapsed four boundaries onto one instant fails every assertion here
/// rather than one.
/// </remarks>
public sealed class StreamRelayBoundaryTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(1);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan FlushCost = TimeSpan.FromSeconds(5);

    private const string RoleChunk = """{"choices":[{"index":0,"delta":{"role":"assistant"}}]}""";
    private const string ContentChunk = """{"choices":[{"index":0,"delta":{"content":"hello"}}]}""";
    private const string ToolChunk =
        """{"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","function":{"name":"f"}}]}}]}""";

    [Fact]
    public async Task The_first_upstream_byte_is_stamped_after_the_read_that_returned_it()
    {
        // The load-bearing case. A clock read before the await describes the moment AgentSplice
        // began waiting, so a runtime that thinks for twenty seconds gets its first byte dated
        // twenty seconds early — and every latency derived from that boundary is wrong by the whole
        // think time.
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Event(ContentChunk), after: Stall), End()]);

        var record = await RelayAsync(clock, body, Sink(clock));

        Assert.True(
            At(record, ObservationType.FirstUpstreamByte) >= Origin + Stall,
            "The first upstream byte was dated before the read that produced it returned.");
    }

    [Fact]
    public async Task The_first_client_event_is_stamped_at_the_flush_that_delivered_it()
    {
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Event(ContentChunk)), End()]);

        var record = await RelayAsync(clock, body, Sink(clock, flushCost: FlushCost));

        var firstByte = At(record, ObservationType.FirstUpstreamByte);
        var flushed = At(record, ObservationType.FirstClientEventFlushed);

        // The write took five seconds to complete, so a boundary that predates its completion is
        // reporting a delivery that had not happened.
        Assert.True(
            flushed >= firstByte + FlushCost,
            FormattableString.Invariant($"The client-event boundary at {flushed} predates the flush that produced it."));
    }

    [Fact]
    public async Task The_boundaries_are_recorded_in_the_order_the_relay_actually_performs_them()
    {
        // Write, then decode, then classify. The previous order asserted decode before flush, which
        // contradicted the pump's own structure: bytes reach the client before anything looks at
        // them.
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Event(ContentChunk)), End()]);

        var record = await RelayAsync(clock, body, Sink(clock, flushCost: FlushCost));

        Assert.Equal(
            [
                ObservationType.FirstUpstreamByte,
                ObservationType.FirstClientEventFlushed,
                ObservationType.FirstDecodedEvent,
                ObservationType.FirstSemanticEvent,
                ObservationType.UpstreamCompleted,
            ],
            Streaming(record));

        AssertNonDecreasing(record);
    }

    [Fact]
    public async Task Each_streaming_boundary_carries_its_own_timestamp()
    {
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Event(ContentChunk)), End()]);

        var record = await RelayAsync(clock, body, Sink(clock, flushCost: FlushCost));

        var timestamps = new[]
        {
            At(record, ObservationType.FirstUpstreamByte),
            At(record, ObservationType.FirstClientEventFlushed),
            At(record, ObservationType.FirstDecodedEvent),
            At(record, ObservationType.FirstSemanticEvent),
        };

        Assert.Equal(timestamps.Length, timestamps.Distinct().Count());
    }

    [Fact]
    public async Task A_keepalive_alone_decodes_an_event_but_delivers_none()
    {
        // A comment holds a connection open. Counting it as the first client event would report a
        // response as having reached the client before it carried anything at all.
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Comment()), End()]);

        var record = await RelayAsync(clock, body, Sink(clock));

        Assert.Contains(ObservationType.FirstDecodedEvent, Types(record));
        Assert.DoesNotContain(ObservationType.FirstClientEventFlushed, Types(record));
        Assert.DoesNotContain(ObservationType.FirstSemanticEvent, Types(record));
    }

    [Fact]
    public async Task A_keepalive_in_an_earlier_read_leaves_the_client_boundary_to_the_data_event()
    {
        var clock = Clock();

        var body = new ScriptedUpstreamBody(
            clock,
            [Chunk(Comment()), Chunk(Event(ContentChunk), after: Stall), End()]);

        var record = await RelayAsync(clock, body, Sink(clock));

        var decoded = At(record, ObservationType.FirstDecodedEvent);
        var flushed = At(record, ObservationType.FirstClientEventFlushed);

        // The keepalive decoded first, so the timeline says so; the client event belongs to the
        // later read, twenty seconds on.
        Assert.True(decoded < flushed, "The keepalive's decode did not precede the first client event.");
        Assert.True(flushed >= Origin + Stall, "The client event was dated from the keepalive's read.");

        Assert.Equal(
            [ObservationType.FirstDecodedEvent, ObservationType.FirstClientEventFlushed],
            Streaming(record).Where(IsFirstBoundary));

        AssertNonDecreasing(record);
    }

    [Fact]
    public async Task A_keepalive_and_a_data_event_in_one_read_stay_in_chronological_order()
    {
        // The awkward case: the keepalive decodes first, but the flush that carried both completed
        // before either decode. Appending as each is learned would put the timeline out of order.
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Comment() + Event(ContentChunk)), End()]);

        var record = await RelayAsync(clock, body, Sink(clock, flushCost: FlushCost));

        Assert.Equal(
            [ObservationType.FirstClientEventFlushed, ObservationType.FirstDecodedEvent],
            Streaming(record).Where(IsFirstBoundary));

        AssertNonDecreasing(record);
    }

    [Fact]
    public async Task The_semantic_boundary_belongs_to_the_event_that_carried_output()
    {
        // FR-STR-012. A role announcement is not output, so a stream whose first chunk announces a
        // role must not report a time to first token measured from it.
        var clock = Clock();

        var body = new ScriptedUpstreamBody(
            clock,
            [Chunk(Event(RoleChunk)), Chunk(Event(ContentChunk), after: Stall), End()]);

        var record = await RelayAsync(clock, body, Sink(clock));

        var decoded = At(record, ObservationType.FirstDecodedEvent);
        var semantic = At(record, ObservationType.FirstSemanticEvent);

        Assert.True(decoded < semantic, "The semantic boundary was taken from the role-only chunk.");
        Assert.True(semantic >= Origin + Stall, "The semantic boundary predates the read that carried output.");
    }

    [Fact]
    public async Task A_tool_call_is_observed_without_disordering_the_first_boundaries()
    {
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Event(ToolChunk)), End()]);

        var record = await RelayAsync(clock, body, Sink(clock, flushCost: FlushCost));

        Assert.Contains(ObservationType.NativeToolCallObserved, Types(record));
        AssertNonDecreasing(record);
    }

    [Fact]
    public async Task The_protocol_terminator_ends_the_relay_without_another_read()
    {
        // The runtime holds the connection open after [DONE]. Reading on would make the client wait
        // for a stall that produced nothing, and would date completion from whatever ended it.
        var clock = Clock();

        var body = new ScriptedUpstreamBody(
            clock,
            [Chunk(Event(ContentChunk) + Event("[DONE]")), Timeout(after: Stall)]);

        var record = await RelayAsync(clock, body, Sink(clock));

        Assert.Equal(1, body.Reads);
        Assert.True(body.Disposed, "The upstream body was not released once the protocol had finished.");

        // Dated from recognising the terminator, so a post-terminator stall cannot stretch the
        // upstream duration or the generation window derived from it.
        Assert.True(At(record, ObservationType.UpstreamCompleted) < Origin + Stall);
        Assert.DoesNotContain(ObservationType.TimeoutFired, Types(record));

        AssertNonDecreasing(record);
    }

    [Fact]
    public async Task The_protocol_terminator_completes_the_exchange_rather_than_failing_it()
    {
        var clock = Clock();

        var body = new ScriptedUpstreamBody(
            clock,
            [Chunk(Event(ContentChunk) + Event("[DONE]")), Timeout(after: Stall)]);

        var outcome = await RelayOutcomeAsync(clock, body, Sink(clock));

        Assert.Equal(StreamTermination.ProtocolTerminatorReceived, outcome.Termination);
        Assert.True(outcome.ProtocolTerminatorObserved);
        Assert.Null(outcome.Error);
        Assert.False(outcome.ClientGone);
    }

    [Fact]
    public async Task A_terminator_in_a_later_read_is_never_consumed()
    {
        // The duplicate-terminator case as a fact about reads rather than about bytes. A second
        // [DONE] is not a second completion, and after the first the relay has stopped asking.
        var clock = Clock();
        var sink = Sink(clock);

        var body = new ScriptedUpstreamBody(
            clock,
            [Chunk(Event(ContentChunk) + Event("[DONE]")), Chunk(Event("[DONE]")), End()]);

        var outcome = await RelayOutcomeAsync(clock, body, sink);

        Assert.Equal(1, body.Reads);
        Assert.Equal(StreamTermination.ProtocolTerminatorReceived, outcome.Termination);

        // Two events delivered: the content chunk and the terminator that ended the response.
        Assert.Equal(2, outcome.ClientEvents);
        Assert.DoesNotContain("[DONE]\n\ndata: [DONE]", sink.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_stream_that_ends_without_a_terminator_still_completes_normally()
    {
        // Preserved deliberately: not every OpenAI-compatible runtime sends the sentinel, and
        // ending without one must not be classified as a terminated stream.
        var clock = Clock();
        var body = new ScriptedUpstreamBody(clock, [Chunk(Event(ContentChunk)), End()]);

        var outcome = await RelayOutcomeAsync(clock, body, Sink(clock));

        Assert.Equal(StreamTermination.NormalCompletion, outcome.Termination);
        Assert.False(outcome.ProtocolTerminatorObserved);
    }

    [Fact]
    public async Task A_sink_that_reports_the_client_gone_without_throwing_ends_the_relay()
    {
        // The defect this covers is specifically non-exceptional: a completed or cancelled pipe is
        // reported through the write's result. A relay that only watched for exceptions kept reading
        // the runtime and kept counting bytes as delivered.
        var clock = Clock();
        var sink = Sink(clock, goneFromWrite: 2);

        var body = new ScriptedUpstreamBody(
            clock,
            [Chunk(Event(ContentChunk)), Chunk(Event(ContentChunk)), Chunk(Event("[DONE]")), End()]);

        var outcome = await RelayOutcomeAsync(clock, body, sink);

        Assert.True(outcome.ClientGone);
        Assert.Equal(StreamTermination.ClientCancelled, outcome.Termination);

        // Stopped at the write that failed: nothing after it was read from the runtime, and nothing
        // after it was counted as delivered.
        Assert.Equal(2, body.Reads);
        Assert.True(body.Disposed);
        Assert.Equal(Event(ContentChunk).Length, outcome.ClientBytes);
    }

    private static bool IsFirstBoundary(ObservationType type) =>
        type is ObservationType.FirstDecodedEvent or ObservationType.FirstClientEventFlushed;

    private static FakeTimeProvider Clock() =>
        new(Origin) { AutoAdvanceAmount = Tick };

    private static RecordingClientSink Sink(
        FakeTimeProvider clock,
        TimeSpan? flushCost = null,
        int? goneFromWrite = null) =>
        new(clock, flushCost ?? TimeSpan.Zero, goneFromWrite);

    private static string Event(string data) => "data: " + data + "\n\n";

    private static string Comment() => ": keepalive\n\n";

    private static UpstreamStep Chunk(string text, TimeSpan? after = null) =>
        new(Encoding.UTF8.GetBytes(text), Failure: null, after ?? TimeSpan.Zero);

    private static UpstreamStep End() => new(Bytes: null, Failure: null, TimeSpan.Zero);

    private static UpstreamStep Timeout(TimeSpan after) =>
        new(
            Bytes: null,
            UpstreamFailure.Create(UpstreamFailureReason.Timeout, phase: TimeoutPhase.IdleStream),
            after);

    private static IEnumerable<ObservationType> Types(ExchangeRecord record) =>
        record.Observations.Select(observation => observation.Type);

    private static IEnumerable<ObservationType> Streaming(ExchangeRecord record) =>
        Types(record).Where(type => type is not ObservationType.NativeToolCallObserved);

    private static DateTimeOffset At(ExchangeRecord record, ObservationType type) =>
        record.Observations.Single(observation => observation.Type == type).Timestamp;

    private static void AssertNonDecreasing(ExchangeRecord record)
    {
        var previous = DateTimeOffset.MinValue;

        foreach (var observation in record.Observations)
        {
            Assert.True(
                observation.Timestamp >= previous,
                FormattableString.Invariant(
                    $"'{observation.Type}' at {observation.Timestamp} precedes the boundary before it at {previous}."));

            previous = observation.Timestamp;
        }
    }

    private static async Task<ExchangeRecord> RelayAsync(
        FakeTimeProvider clock,
        ScriptedUpstreamBody body,
        RecordingClientSink sink)
    {
        var recorder = Recorder(clock);

        await RelayAsync(clock, recorder, body, sink).ConfigureAwait(false);

        return recorder.ToRecord();
    }

    private static Task<StreamRelayOutcome> RelayOutcomeAsync(
        FakeTimeProvider clock,
        ScriptedUpstreamBody body,
        RecordingClientSink sink) =>
        RelayAsync(clock, Recorder(clock), body, sink);

    private static Task<StreamRelayOutcome> RelayAsync(
        FakeTimeProvider clock,
        ExchangeRecorder recorder,
        ScriptedUpstreamBody body,
        RecordingClientSink sink)
    {
        var relay = new ChatCompletionStreamRelay(
            new OpenAiStreamEventInterpreter(),
            new OpenAiChatCompletionResponseCodec(),
            Options.Create(new AgentSpliceOptions()),
            clock);

        var metadata = UpstreamResponseMetadata.Create(200, clock.GetUtcNow(), "text/event-stream");

        var correlation = new GatewayCorrelation(
            recorder.RequestId,
            recorder.ExchangeId,
            recorder.TraceId,
            RuntimeEndpointId.Create("runtime"));

        return relay.RelayAsync(
            recorder,
            ProviderStreamResult.FromResponse(metadata, body),
            sink,
            correlation,
            CancellationToken.None);
    }

    private static ExchangeRecorder Recorder(TimeProvider clock)
    {
        var exchangeId = ExchangeId.New();
        var recorder = new ExchangeRecorder(exchangeId, PublicRequestId.FromExchangeId(exchangeId), clock);

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: true, clock.GetUtcNow());

        return recorder;
    }

    /// <summary>One scripted upstream read, and how much time passes inside it.</summary>
    private sealed record UpstreamStep(byte[]? Bytes, UpstreamFailure? Failure, TimeSpan Elapsed);

    /// <summary>
    /// An upstream body that hands out a scripted sequence of reads and counts how many were asked
    /// for.
    /// </summary>
    /// <remarks>
    /// The read count is the assertion for "no further read after the terminator". Asserting on the
    /// bytes the client received cannot express it: a runtime that stops writing looks exactly like
    /// a gateway that stops reading.
    /// </remarks>
    private sealed class ScriptedUpstreamBody : IUpstreamResponseBody
    {
        private readonly FakeTimeProvider clock;
        private readonly Queue<UpstreamStep> steps;

        internal ScriptedUpstreamBody(FakeTimeProvider clock, IEnumerable<UpstreamStep> steps)
        {
            this.clock = clock;
            this.steps = new Queue<UpstreamStep>(steps);
        }

        internal int Reads { get; private set; }

        internal bool Disposed { get; private set; }

        public ValueTask<UpstreamReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            Reads++;

            if (!steps.TryDequeue(out var step))
            {
                throw new InvalidOperationException(
                    "The relay read past the end of the script, which the test exists to detect.");
            }

            // Time passes inside the read, exactly as it does while a runtime is thinking. A
            // timestamp taken before this call is a claim about a moment that had not happened.
            clock.Advance(step.Elapsed);

            if (step.Failure is { } failure)
            {
                return ValueTask.FromResult(UpstreamReadResult.Failed(failure));
            }

            if (step.Bytes is not { } bytes)
            {
                return ValueTask.FromResult(UpstreamReadResult.Completed);
            }

            bytes.CopyTo(buffer);

            return ValueTask.FromResult(UpstreamReadResult.Bytes(bytes.Length));
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;

            return ValueTask.CompletedTask;
        }
    }

    /// <summary>A client sink whose writes cost time and can report the client gone without throwing.</summary>
    private sealed class RecordingClientSink : IClientResponseSink
    {
        private readonly FakeTimeProvider clock;
        private readonly TimeSpan flushCost;
        private readonly int? goneFromWrite;
        private readonly List<byte> written = [];

        internal RecordingClientSink(FakeTimeProvider clock, TimeSpan flushCost, int? goneFromWrite)
        {
            this.clock = clock;
            this.flushCost = flushCost;
            this.goneFromWrite = goneFromWrite;
        }

        public bool HasStarted { get; private set; }

        internal bool Aborted { get; private set; }

        internal string Text => Encoding.UTF8.GetString(written.ToArray());

        public ValueTask<ClientWriteResult> StartAsync(
            ClientResponseStart start,
            CancellationToken cancellationToken)
        {
            HasStarted = true;

            return ValueTask.FromResult(ClientWriteResult.Written);
        }

        public ValueTask<ClientWriteResult> WriteAsync(
            ReadOnlyMemory<byte> bytes,
            CancellationToken cancellationToken)
        {
            Writes++;

            // The flush completes here, so the clock moves before the result is returned. A boundary
            // taken from before the write cannot land on the far side of this.
            clock.Advance(flushCost);

            if (Writes >= goneFromWrite)
            {
                return ValueTask.FromResult(ClientWriteResult.ClientGone);
            }

            written.AddRange(bytes.ToArray());

            return ValueTask.FromResult(ClientWriteResult.Written);
        }

        internal int Writes { get; private set; }

        public void Abort() => Aborted = true;
    }
}
