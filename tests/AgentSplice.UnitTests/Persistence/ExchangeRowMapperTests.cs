using System.Text.Json;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.Infrastructure.Persistence;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentSplice.UnitTests.Persistence;

/// <summary>
/// What survives the trip into the store, and what deliberately does not.
/// </summary>
/// <remarks>
/// The interesting assertions are all negative. Anyone can store a value; the rules worth a test are
/// that an unobserved boundary produces no row, that an unreported token count stays null instead of
/// becoming a zero, and that a request which never named a model is still listable without one
/// (FR-TRACE-006, FR-OBS-003, ADR 0008).
/// </remarks>
public sealed class ExchangeRowMapperTests
{
    private static readonly DateTimeOffset Origin = new(2026, 8, 1, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_completed_exchange_stores_its_identity_routing_and_outcome()
    {
        var recorder = Recorder(out var clock);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("gpt-oss-20b"), streaming: false, Origin);
        recorder.Update(exchange => exchange.Resolve(ModelResolution.FromAlias(
            ClientModelId.Create("gpt-oss-20b"),
            ModelAliasId.Create("gpt-oss-20b"),
            RuntimeEndpointId.Create("lmstudio-local"),
            UpstreamModelId.Create("openai/gpt-oss-20b"))));

        clock.Advance(TimeSpan.FromSeconds(3));
        recorder.Complete();

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());

        Assert.Equal(recorder.ExchangeId.Value, row.ExchangeId);
        Assert.Equal("gpt-oss-20b", row.ClientModelId);
        Assert.Equal("lmstudio-local", row.RuntimeEndpointId);
        Assert.Equal("openai/gpt-oss-20b", row.UpstreamModelId);
        Assert.Equal((int)ModelResolutionSource.ConfiguredAlias, row.ResolutionSource);
        Assert.Equal((int)ExchangeStatus.Completed, row.Status);
        Assert.Equal(Origin.UtcTicks, row.StartedAtTicks);
        Assert.Equal(Origin.AddSeconds(3).UtcTicks, row.CompletedAtTicks);
        Assert.Null(row.FailureClass);
    }

    [Fact]
    public void A_request_that_failed_before_its_model_was_known_is_still_a_row()
    {
        // The gap ADR 0008 left for this stage. CompletionExchange.Accept requires a valid model, so
        // a request naming an unknown one produces no exchange at all — and that is the single most
        // common misconfiguration a client has. Refusing to store it would mean the one case an
        // operator most needs evidence for is the one case with none.
        var recorder = Recorder(out _);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Fail(GatewayErrorCatalogue.For(FailureClass.ModelNotFound));

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());

        Assert.Equal(Origin.UtcTicks, row.StartedAtTicks);
        Assert.Equal((int)ExchangeStatus.Failed, row.Status);
        Assert.Equal((int)FailureClass.ModelNotFound, row.FailureClass);
        Assert.Equal(ErrorCodes.ModelNotFound, row.ErrorCode);

        // Not an empty string and not a placeholder. The client never named a model AgentSplice
        // recognised, and the store says so.
        Assert.Null(row.ClientModelId);
        Assert.Null(row.RuntimeEndpointId);

        // Nor did it state a streaming preference: the request was refused before its envelope was
        // read, so false would be a claim and null is the fact.
        Assert.Null(row.Streaming);
    }

    [Fact]
    public void A_disconnect_before_the_model_was_known_is_cancelled_rather_than_failed()
    {
        // Such a record reaches the sink with no exchange and no error, so classifying from the
        // absence of an error would file a client disconnect as a gateway fault.
        var recorder = Recorder(out _);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Cancel();

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());

        Assert.Equal((int)ExchangeStatus.Cancelled, row.Status);
    }

    [Fact]
    public void A_failure_with_no_completion_boundary_stores_no_completion_time()
    {
        // ExchangeRecorder.Fail appends no boundary of its own, so nothing observed when the
        // exchange ended. A terminal status with a null completion time says exactly that; filling
        // it with the current clock would invent a boundary the timeline does not contain.
        var recorder = Recorder(out var clock);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.Fail(GatewayErrorCatalogue.For(FailureClass.ModelNotFound));

        Assert.Null(ExchangeRowMapper.ToRow(recorder.ToRecord()).CompletedAtTicks);
    }

    [Fact]
    public void Unreported_usage_is_absent_rather_than_zero()
    {
        var recorder = Recorder(out _);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("m"), streaming: false, Origin);
        recorder.Complete();

        // Zero would be a claim that no tokens were consumed. Absence is the claim that AgentSplice
        // does not know, and the two must not be stored the same way (FR-OBS-003).
        Assert.Null(ExchangeRowMapper.ToRow(recorder.ToRecord()).UsageJson);
    }

    [Fact]
    public void Reported_usage_keeps_the_provenance_of_every_component()
    {
        var recorder = Recorder(out _);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("m"), streaming: false, Origin);
        recorder.Update(exchange => exchange.WithUsage(UsageObservation.Create(
            TokenCount.FromUpstream(11),
            TokenCount.FromGatewayEstimate(22))));
        recorder.Complete();

        var usage = JsonDocument.Parse(ExchangeRowMapper.ToRow(recorder.ToRecord()).UsageJson!).RootElement;

        Assert.Equal(11, usage.GetProperty("promptTokens").GetProperty("value").GetInt32());
        Assert.Equal(
            "upstream_reported",
            usage.GetProperty("promptTokens").GetProperty("provenance").GetString());

        // The estimate must not be silently upgraded to match the reported count beside it.
        Assert.Equal(
            "estimated",
            usage.GetProperty("completionTokens").GetProperty("provenance").GetString());

        // Never computed from the other two: a runtime may report a total that includes tokens
        // AgentSplice cannot see, so an absent total stays absent.
        Assert.False(usage.TryGetProperty("totalTokens", out _));
    }

    [Fact]
    public void Every_observed_boundary_becomes_a_row_in_sequence_order()
    {
        var recorder = Recorder(out var clock);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Observe(ObservationType.ValidationCompleted, Origin.AddMilliseconds(1));
        recorder.Accept(ClientModelId.Create("m"), streaming: false, Origin);
        clock.Advance(TimeSpan.FromMilliseconds(5));
        recorder.Complete();

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());
        var sequences = row.Observations.Select(observation => observation.Sequence).ToArray();

        Assert.Equal([0, 1, 2], sequences);
        Assert.Equal((int)ObservationType.RequestAccepted, row.Observations.First().Type);
    }

    [Fact]
    public void An_observation_without_details_stores_no_detail_document()
    {
        // "{}" and null are different answers: one says the boundary carried an empty detail map,
        // the other that it carried none.
        var recorder = Recorder(out _);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Observe(ObservationType.ModelResolved, SafeDetails.Create("runtime.id", "lmstudio-local"));

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());

        Assert.Null(row.Observations.First().DetailsJson);
        Assert.Contains(
            "lmstudio-local",
            row.Observations.Last().DetailsJson,
            StringComparison.Ordinal);
    }

    [Fact]
    public void A_phase_that_was_not_observed_produces_no_measurement_row()
    {
        var recorder = Recorder(out var clock);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("m"), streaming: false, Origin);
        clock.Advance(TimeSpan.FromSeconds(2));
        recorder.Complete();

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());
        var names = row.Measurements.Select(measurement => measurement.Name).ToArray();

        Assert.Contains(MeasurementNames.TotalDuration, names, StringComparer.Ordinal);

        // No upstream request was ever opened, so there is no headers phase to have taken no time.
        Assert.DoesNotContain(MeasurementNames.UpstreamHeadersDuration, names, StringComparer.Ordinal);
        Assert.DoesNotContain(MeasurementNames.TimeToFirstUpstreamByte, names, StringComparer.Ordinal);
    }

    [Fact]
    public void A_measurement_stores_the_provenance_it_arrived_with()
    {
        var recorder = Recorder(out var clock);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("m"), streaming: false, Origin);
        recorder.Update(exchange => exchange.WithUsage(
            UsageObservation.Create(promptTokens: TokenCount.FromGatewayEstimate(7))));
        clock.Advance(TimeSpan.FromSeconds(1));
        recorder.Complete();

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());
        var prompt = row.Measurements.Single(measurement =>
            string.Equals(measurement.Name, MeasurementNames.PromptTokens, StringComparison.Ordinal));

        // An estimate stored as Measured would be indistinguishable from a clock reading forever
        // afterwards, including in a replay comparison (P-008).
        Assert.Equal((int)MeasurementProvenance.Estimated, prompt.Provenance);

        var total = row.Measurements.Single(measurement =>
            string.Equals(measurement.Name, MeasurementNames.TotalDuration, StringComparison.Ordinal));

        Assert.Equal((int)MeasurementProvenance.Measured, total.Provenance);
    }

    [Fact]
    public void The_relayable_content_type_never_reaches_the_store()
    {
        // UpstreamResponseMetadata states outright that the verbatim header must not reach evidence:
        // it is unbounded runtime-chosen text whose only destination is the wire. The store keeps
        // the normalised token instead.
        var recorder = Recorder(out _);
        recorder.Observe(ObservationType.RequestAccepted, Origin);
        recorder.Accept(ClientModelId.Create("m"), streaming: false, Origin);
        recorder.Update(exchange => exchange.WithUpstreamResponse(UpstreamResponseMetadata.Create(
            200,
            Origin,
            "text/event-stream; charset=utf-8; boundary=" + new string('x', 300))));
        recorder.Complete();

        var row = ExchangeRowMapper.ToRow(recorder.ToRecord());

        Assert.Equal("text/event-stream", row.UpstreamMediaType);
        Assert.Equal(200, row.UpstreamStatusCode);
    }

    private static ExchangeRecorder Recorder(out FakeTimeProvider clock)
    {
        clock = new FakeTimeProvider(Origin);
        var exchangeId = ExchangeId.New();

        return new ExchangeRecorder(exchangeId, PublicRequestId.FromExchangeId(exchangeId), clock);
    }
}
