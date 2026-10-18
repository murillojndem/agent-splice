using AgentSplice.Application.Errors;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Runtimes;
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

        // Nor does it leave an error code behind for a record that completed: the refused call used
        // to overwrite the error before its transition was refused.
        Assert.Null(recorder.ToRecord().Error);
        Assert.Null(recorder.Failure);
    }

    [Fact]
    public void A_transition_the_domain_refuses_leaves_the_exchange_open_to_end_properly()
    {
        // A streamed exchange must say how its stream ended, so completing it without a termination
        // throws. Marking it terminated before the transition left a row that was never terminal and
        // an error no envelope carried; computed first, the catch-all can still end it.
        var recorder = Recorder(new FakeTimeProvider(Origin));

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: true, Origin);
        recorder.BeginStreaming();

        Assert.Throws<ArgumentException>(() => recorder.Complete(StreamTermination.NotApplicable));
        Assert.False(recorder.IsTerminated);
        Assert.DoesNotContain(recorder.ToRecord().Observations, o => o.Type == ObservationType.ClientCompleted);

        recorder.Abandon(GatewayErrorCatalogue.For(FailureClass.InternalError), StreamTermination.Unknown);

        Assert.Equal(ExchangeStatus.Failed, recorder.Exchange?.Status);
        Assert.Equal(FailureClass.InternalError, recorder.Exchange?.FailureClass);
    }

    [Fact]
    public void An_abandoned_response_records_why_it_failed_and_no_error_code()
    {
        // No envelope reached the client, so no code may be recorded as though one had: the failure
        // class and the termination say why the response stopped (ADR 0025).
        var clock = new FakeTimeProvider(Origin);
        var recorder = Recorder(clock);

        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("local-coder"), streaming: true, Origin);
        recorder.BeginStreaming();

        clock.Advance(TimeSpan.FromSeconds(2));
        recorder.Abandon(GatewayErrorCatalogue.For(FailureClass.UpstreamTimeout), StreamTermination.Timeout);

        var record = recorder.ToRecord();

        Assert.Null(record.Error);
        Assert.Equal(FailureClass.UpstreamTimeout, record.Exchange?.FailureClass);
        Assert.Equal(StreamTermination.Timeout, record.Exchange?.StreamTermination);
        Assert.Equal("upstream_timeout_error", recorder.Failure?.Type);

        var abandoned = Assert.Single(record.Observations, o => o.Type == ObservationType.ClientResponseAbandoned);

        Assert.Equal(Origin.AddSeconds(2), abandoned.Timestamp);
        Assert.DoesNotContain(record.Observations, o => o.Type == ObservationType.ClientCompleted);
    }

    [Fact]
    public void A_failure_whose_envelope_was_written_records_its_code_and_says_the_client_got_it()
    {
        // The positive fact of delivery. Without it a failure whose 504 reached the client and one
        // whose connection was reset read the same, field for field.
        var recorder = Recorder(new FakeTimeProvider(Origin));

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: false, Origin);
        recorder.Fail(GatewayErrorCatalogue.For(FailureClass.UpstreamTimeout));

        var record = recorder.ToRecord();

        Assert.Equal("agentsplice_upstream_timeout", record.Error?.Code);

        var completed = Assert.Single(record.Observations, o => o.Type == ObservationType.ClientCompleted);

        Assert.Equal("gateway_error", completed.Details.Values["client.response"]);
    }

    [Fact]
    public void A_relayed_answer_says_so_on_its_completion()
    {
        var recorder = Recorder(new FakeTimeProvider(Origin));

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: false, Origin);
        recorder.Complete();

        var completed = Assert.Single(recorder.ToRecord().Observations, o => o.Type == ObservationType.ClientCompleted);

        Assert.Equal("relayed", completed.Details.Values["client.response"]);
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("abandoned")]
    [InlineData("failed")]
    public void Every_ending_has_a_total_duration_from_its_own_client_boundary(string ending)
    {
        // The exported duration histogram counted failed and cancelled exchanges while their records
        // carried no total at all, because it ran only to a completion boundary.
        var clock = new FakeTimeProvider(Origin);
        var recorder = Recorder(clock);

        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("local-coder"), streaming: false, Origin);

        clock.Advance(TimeSpan.FromSeconds(3));

        switch (ending)
        {
            case "cancelled":
                recorder.Cancel();
                break;
            case "abandoned":
                recorder.Abandon(GatewayErrorCatalogue.For(FailureClass.InvalidUpstreamResponse), StreamTermination.NotApplicable);
                break;
            default:
                recorder.Fail(GatewayErrorCatalogue.For(FailureClass.RuntimeUnavailable));
                break;
        }

        var total = Assert.Single(recorder.ToRecord().Measurements, m => m.Name == MeasurementNames.TotalDuration);

        Assert.Equal(3000d, total.Value, 3);
        Assert.Equal(Origin.AddSeconds(3), recorder.Exchange?.CompletedAt);
    }

    [Fact]
    public void No_bytes_are_claimed_delivered_for_an_answer_that_was_not()
    {
        // The runtime's body length is a fact about the runtime. Reported as bytes forwarded to a
        // client that left during the write, it claimed a delivery nobody observed.
        var recorder = Recorder(new FakeTimeProvider(Origin));

        recorder.Accept(ClientModelId.Create("local-coder"), streaming: false, Origin);
        recorder.Update(exchange => exchange.WithResponseSummary(Summary(streamEventCount: 0)));
        recorder.Cancel();

        Assert.DoesNotContain(
            recorder.ToRecord().Measurements,
            measurement => measurement.Name == MeasurementNames.ClientResponseBytes);
    }

    [Fact]
    public void A_timeout_is_recorded_with_its_phase_and_any_other_failure_with_its_classification()
    {
        var recorder = Recorder(new FakeTimeProvider(Origin));

        recorder.ObserveUpstreamFailure(UpstreamFailure.Timeout(TimeoutPhase.Connect));
        recorder.ObserveUpstreamFailure(UpstreamFailure.Create(
            UpstreamFailureReason.ResponseTooLarge,
            details: SafeDetails.Create("upstream.completion", "body.too_large")));

        var observations = recorder.ToRecord().Observations;

        Assert.Equal("Connect", Assert.Single(observations, o => o.Type == ObservationType.TimeoutFired).Details.Values["timeout.phase"]);

        var failed = Assert.Single(observations, o => o.Type == ObservationType.UpstreamFailed);

        Assert.Equal("ResponseTooLarge", failed.Details.Values["upstream.failure"]);
        Assert.Equal("body.too_large", failed.Details.Values["upstream.completion"]);
    }

    [Fact]
    public void A_cancelled_upstream_call_records_no_failure_of_its_own()
    {
        // The client's own departure. The cancellation boundary says so, and an upstream failure next
        // to it would blame the runtime for it.
        var recorder = Recorder(new FakeTimeProvider(Origin));

        recorder.ObserveUpstreamFailure(UpstreamFailure.Create(UpstreamFailureReason.Cancelled));

        Assert.Empty(recorder.ToRecord().Observations);
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
        recorder.RecordRelayedToClient(4096);
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
