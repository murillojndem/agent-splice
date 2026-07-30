using AgentSplice.Application.Errors;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Exchanges;

/// <summary>
/// What the recorder guarantees about the evidence it produces (FR-TRACE-005, FR-TRACE-006,
/// FR-OBS-004, FR-OBS-010).
/// </summary>
/// <remarks>
/// The weak form of every test here would assert that a boundary exists. That proves nothing: a
/// boundary stamped at the wrong moment still exists, and the resulting measurement is confidently
/// wrong rather than absent — which is worse than no measurement at all, because a reader has no way
/// to tell. So each test asserts <em>when</em> a boundary was taken and <em>whether</em> a
/// measurement was derived, not that either happened.
/// </remarks>
public sealed class ExchangeRecorderTests
{
    private static readonly DateTimeOffset Origin = new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_boundary_can_be_stamped_at_a_moment_observed_elsewhere()
    {
        // The provider observes response headers while the orchestrator is still awaiting the call.
        // Without this overload the boundary lands when control returns, and "time to headers"
        // silently becomes "time until everything had arrived".
        var clock = new FakeTimeProvider(Origin);
        var recorder = Recorder(clock);
        var observed = Origin.AddMilliseconds(40);

        clock.Advance(TimeSpan.FromSeconds(5));
        recorder.Observe(ObservationType.UpstreamHeadersReceived, observed);

        var record = recorder.ToRecord();
        var boundary = Assert.Single(record.Observations);

        Assert.Equal(observed, boundary.Timestamp);
        Assert.NotEqual(clock.GetUtcNow(), boundary.Timestamp);
    }

    [Fact]
    public void An_explicitly_stamped_boundary_is_still_a_gateway_observation()
    {
        // The timestamp came from AgentSplice's own clock, read closer to the event. Marking it
        // Upstream would claim the runtime reported it, which is a claim about a different source
        // of evidence and would survive into replay and conformance reports.
        var recorder = Recorder(new FakeTimeProvider(Origin));

        recorder.Observe(ObservationType.FirstUpstreamByte, Origin.AddMilliseconds(12));

        Assert.Equal(ObservationSource.Gateway, recorder.ToRecord().Observations[0].Source);
    }

    [Fact]
    public void A_regressed_clock_leaves_the_timeline_intact_and_the_measurement_absent()
    {
        // Wall-clock timestamps can be ordered impossibly if the host clock steps backwards. The
        // anomaly must stay visible in the timeline — clamping it would hide a real problem — while
        // no duration is derived from it, because a negative latency is not evidence of anything.
        var clock = new FakeTimeProvider(Origin);
        var recorder = Recorder(clock);

        recorder.Observe(ObservationType.UpstreamRequestOpened, Origin.AddSeconds(2));
        recorder.Observe(ObservationType.UpstreamHeadersReceived, Origin.AddSeconds(1));

        var record = recorder.ToRecord();

        Assert.Equal(2, record.Observations.Count);
        Assert.Equal(
            TimeSpan.FromSeconds(-1),
            recorder.DurationBetween(
                ObservationType.UpstreamRequestOpened,
                ObservationType.UpstreamHeadersReceived));

        Assert.DoesNotContain(
            record.Measurements,
            measurement => measurement.Name == MeasurementNames.UpstreamHeadersDuration);
    }

    [Fact]
    public void An_exchange_may_be_handed_to_the_sink_only_once()
    {
        // A fault raised after the response was written reaches the orchestrator's catch-all, which
        // would otherwise record the same exchange a second time with a different ending. One
        // request must leave exactly one account of itself.
        var recorder = Recorder(new FakeTimeProvider(Origin));

        Assert.True(recorder.TryBeginRecording());
        Assert.False(recorder.TryBeginRecording());
    }

    [Fact]
    public void A_second_terminal_transition_does_not_overwrite_the_first()
    {
        var clock = new FakeTimeProvider(Origin);
        var recorder = Recorder(clock);

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: false, Origin);
        recorder.Complete();

        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.Fail(GatewayErrorCatalogue.For(FailureClass.InternalError));

        Assert.Equal(ExchangeStatus.Completed, recorder.Exchange?.Status);
        Assert.Null(recorder.Exchange?.FailureClass);
    }

    [Fact]
    public void A_completed_stream_derives_generation_throughput_but_never_prompt_throughput()
    {
        // The generation window is observable: the first semantic output event to upstream
        // completion. No boundary separates prompt processing from anything else, so prompt
        // throughput has no interval that is not borrowed from another phase (FR-OBS-005).
        var recorder = StreamedExchange(usage: UsageObservation.Create(
            promptTokens: TokenCount.FromUpstream(500),
            completionTokens: TokenCount.FromUpstream(40)));

        var measurements = recorder.ToRecord().Measurements;

        var generation = Assert.Single(
            measurements,
            measurement => measurement.Name == MeasurementNames.GenerationThroughput);

        Assert.Equal(20d, generation.Value, 3);
        Assert.Equal(MeasurementUnit.TokensPerSecond, generation.Unit);

        // The duration is measured but the token count is only reported, and a derived value can
        // never be stronger than its weakest input.
        Assert.Equal(MeasurementProvenance.UpstreamReported, generation.Provenance);

        Assert.DoesNotContain(
            measurements,
            measurement => measurement.Name == MeasurementNames.PromptThroughput);
    }

    [Fact]
    public void A_stream_whose_runtime_reported_no_usage_derives_no_throughput()
    {
        // Unknown stays unknown. Zero tokens over a real interval would drag any aggregate down
        // while saying nothing about how fast the runtime generated (FR-OBS-004).
        var recorder = StreamedExchange(usage: UsageObservation.Unknown);

        Assert.DoesNotContain(
            recorder.ToRecord().Measurements,
            measurement => measurement.Name == MeasurementNames.GenerationThroughput);
    }

    [Fact]
    public void A_buffered_exchange_reports_no_stream_event_count()
    {
        // Not zero: a buffered response has no events to count, and a zero would read as "it
        // streamed, and produced nothing".
        var clock = new FakeTimeProvider(Origin);
        var recorder = Recorder(clock);

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: false, Origin);
        recorder.Update(exchange => exchange.WithResponseSummary(Summary(streamEventCount: 0)));
        recorder.Complete();

        var measurements = recorder.ToRecord().Measurements;

        Assert.Contains(measurements, measurement => measurement.Name == MeasurementNames.ClientResponseBytes);
        Assert.DoesNotContain(measurements, measurement => measurement.Name == MeasurementNames.ClientStreamEvents);
    }

    private static ExchangeRecorder StreamedExchange(UsageObservation usage)
    {
        var clock = new FakeTimeProvider(Origin);
        var recorder = Recorder(clock);

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: true, Origin);
        recorder.BeginStreaming();

        recorder.Observe(ObservationType.FirstSemanticEvent, Origin.AddSeconds(1));
        recorder.Observe(ObservationType.UpstreamCompleted, Origin.AddSeconds(3));

        recorder.Update(exchange => exchange
            .WithResponseSummary(Summary(streamEventCount: 12))
            .WithUsage(usage));

        clock.SetUtcNow(Origin.AddSeconds(3));
        recorder.Complete(StreamTermination.ProtocolTerminatorReceived);

        return recorder;
    }

    private static StructuralResponseSummary Summary(int streamEventCount) =>
        StructuralResponseSummary.Create(
            choiceCount: 1,
            finishReasons: ["stop"],
            nativeToolCallCount: 0,
            responseBodyBytes: 4096,
            streamEventCount: streamEventCount,
            usageReported: true);

    private static ExchangeRecorder Recorder(TimeProvider timeProvider) =>
        new(ExchangeId.New(), PublicRequestId.New(), timeProvider);
}
