using System.Text;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// The evidence a real proxied completion leaves behind
/// (docs/SPECIFICATION.md FR-TRACE-002 to FR-TRACE-007, FR-TRACE-010).
/// </summary>
/// <remarks>
/// The absence assertions matter as much as the presence ones. A boundary AgentSplice cannot observe
/// must stay absent rather than being filled in with a plausible timestamp, because a fabricated
/// boundary is worse than a missing one: it looks like evidence.
/// </remarks>
public sealed class ExchangeEvidenceTests
{
    private const string Completion = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"qwen3.6-27b-mtp",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":41,"completion_tokens":7,"total_tokens":48}}
        """;

    [Fact]
    public async Task A_completion_records_the_expected_boundaries_in_order()
    {
        var record = await ProxyAsync();

        Assert.Equal(
            [
                ObservationType.RequestAccepted,
                ObservationType.RequestBodyRead,
                ObservationType.ValidationCompleted,
                ObservationType.StructuralSummaryCreated,
                ObservationType.ModelResolved,
                ObservationType.RoutingApplied,
                ObservationType.UpstreamRequestOpened,

                // This exchange is the first to use its runtime's client, so it pays for the
                // connection. A later request served by the pool records neither boundary.
                ObservationType.UpstreamConnectionStarted,
                ObservationType.UpstreamConnectionEstablished,
                ObservationType.UpstreamHeadersReceived,
                ObservationType.FirstUpstreamByte,
                ObservationType.UpstreamCompleted,
                ObservationType.ClientCompleted,
            ],
            record.Observations.Select(observation => observation.Type));
    }

    [Fact]
    public async Task The_structural_summary_is_recorded_before_the_model_is_resolved()
    {
        // So that a request naming an unknown model still leaves safe evidence of what arrived.
        var record = await ProxyAsync();
        var types = record.Observations.Select(observation => observation.Type).ToList();

        Assert.True(
            types.IndexOf(ObservationType.StructuralSummaryCreated)
            < types.IndexOf(ObservationType.ModelResolved));
    }

    [Fact]
    public async Task Observations_are_sequence_ordered_from_zero()
    {
        var record = await ProxyAsync();

        Assert.Equal(
            Enumerable.Range(0, record.Observations.Count),
            record.Observations.Select(observation => observation.Sequence));
    }

    [Fact]
    public async Task Streaming_only_boundaries_stay_absent()
    {
        // There are no SSE frames to decode, and a buffered body arrives as one unit, so a semantic
        // or first-flush boundary would be indistinguishable from completion.
        var record = await ProxyAsync();

        AssertAbsent(
            record,
            ObservationType.FirstDecodedEvent,
            ObservationType.FirstSemanticEvent,
            ObservationType.FirstClientEventFlushed);
    }

    [Fact]
    public async Task Persistence_boundaries_stay_absent()
    {
        // Stage 1A queues nothing, so nothing was queued, persisted, or attempted.
        var record = await ProxyAsync();

        AssertAbsent(
            record,
            ObservationType.MetadataQueued,
            ObservationType.PersistenceCompleted,
            ObservationType.PersistenceFailed);
    }

    [Fact]
    public async Task A_routing_change_names_the_alias_and_the_runtime()
    {
        var record = await ProxyAsync();
        var routing = record.Observations.Single(o => o.Type == ObservationType.RoutingApplied);

        Assert.Equal("local-coder", routing.Details.Values["alias.id"]);
        Assert.Equal(GatewayFixture.RuntimeId, routing.Details.Values["runtime.id"]);
        Assert.Equal("true", routing.Details.Values["body.rewritten"]);
    }

    [Fact]
    public async Task An_alias_that_renames_nothing_still_records_the_routing_decision()
    {
        // Selecting a runtime is a routing decision even when not one byte of the body moves.
        var record = await ProxyAsync(settings =>
        {
            settings[GatewayFixture.AliasKey(0, "id")] = "qwen3.6-27b-mtp";
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
        },
        model: "qwen3.6-27b-mtp");

        var routing = record.Observations.Single(o => o.Type == ObservationType.RoutingApplied);

        Assert.Equal("false", routing.Details.Values["body.rewritten"]);
    }

    [Fact]
    public async Task The_exchange_records_what_the_client_asked_for_and_what_was_forwarded()
    {
        var record = await ProxyAsync();
        var exchange = record.Exchange!;

        Assert.Equal("local-coder", exchange.ClientModelId.Value);
        Assert.Equal("qwen3.6-27b-mtp", exchange.UpstreamModelId?.Value);
        Assert.Equal(GatewayFixture.RuntimeId, exchange.RuntimeEndpointId?.Value);
        Assert.Equal(IngressProtocol.OpenAiChatCompletions, exchange.IngressProtocol);
        Assert.False(exchange.Streaming);
    }

    [Fact]
    public async Task A_relayed_success_completes_with_no_failure_class()
    {
        var record = await ProxyAsync();

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.Null(record.Exchange.FailureClass);
        Assert.Equal(StreamTermination.NotApplicable, record.Exchange.StreamTermination);
    }

    [Fact]
    public async Task Content_retention_is_disabled_by_default()
    {
        var record = await ProxyAsync();

        Assert.Equal(ContentRetentionState.Disabled, record.Exchange!.ContentRetentionState);
    }

    [Fact]
    public async Task The_upstream_status_is_recorded_separately_from_the_body_summary()
    {
        var record = await ProxyAsync();

        Assert.Equal(200, record.Exchange!.UpstreamResponse?.StatusCode);
        Assert.Equal("2xx", record.Exchange.UpstreamResponse?.StatusClass);
        Assert.Equal("application/json", record.Exchange.UpstreamResponse?.ContentType);
    }

    [Fact]
    public async Task Usage_is_recorded_with_upstream_provenance()
    {
        var record = await ProxyAsync();

        Assert.Equal(41, record.Exchange!.Usage.PromptTokens?.Value);
        Assert.Equal(MeasurementProvenance.UpstreamReported, record.Exchange.Usage.WeakestProvenance());
    }

    [Fact]
    public async Task Nothing_is_recorded_as_dropped()
    {
        var record = await ProxyAsync();

        Assert.Empty(record.Exchange!.RequestSummary!.DroppedFieldNames);
    }

    [Fact]
    public async Task No_prompt_content_reaches_an_observation_detail()
    {
        const string Sentinel = "SENTINEL-PROMPT-abc123";

        var record = await ProxyAsync(
            body: $$"""{"model":"local-coder","messages":[{"role":"user","content":"{{Sentinel}}"}]}""");

        foreach (var observation in record.Observations)
        {
            foreach (var value in observation.Details.Values.Values)
            {
                Assert.DoesNotContain(Sentinel, value, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public async Task Latency_phases_are_recorded_as_measurements_that_carry_their_provenance()
    {
        // A histogram records a number; a Measurement records a number and where it came from. P-008
        // is the reason the second exists at all.
        var record = await ProxyAsync();

        Assert.Contains(record.Measurements, m => m.Name == MeasurementNames.TotalDuration);
        Assert.Contains(record.Measurements, m => m.Name == MeasurementNames.UpstreamHeadersDuration);
        Assert.All(record.Measurements, m => Assert.Equal(record.ExchangeId, m.ExchangeId));
        Assert.All(record.Measurements, m => Assert.True(double.IsFinite(m.Value)));
    }

    [Fact]
    public async Task A_duration_is_measured_while_a_token_count_stays_upstream_reported()
    {
        // A clock reading and a runtime's claim are different kinds of evidence, and a token count
        // must never be silently upgraded to "measured".
        var record = await ProxyAsync();

        Assert.Equal(
            MeasurementProvenance.Measured,
            record.Measurements.Single(m => m.Name == MeasurementNames.TotalDuration).Provenance);
        Assert.Equal(
            MeasurementProvenance.UpstreamReported,
            record.Measurements.Single(m => m.Name == MeasurementNames.PromptTokens).Provenance);
    }

    [Fact]
    public async Task No_throughput_measurement_is_derived_from_a_non_streamed_exchange()
    {
        // There is no boundary separating prompt processing from generation, so a throughput value
        // would have to borrow one interval for the other (FR-OBS-005).
        var record = await ProxyAsync();

        Assert.DoesNotContain(
            record.Measurements,
            m => m.Name == MeasurementNames.PromptThroughput || m.Name == MeasurementNames.GenerationThroughput);
    }

    [Fact]
    public async Task An_unobserved_phase_produces_no_measurement_rather_than_a_zero()
    {
        var record = await ProxyAsync();

        Assert.DoesNotContain(
            record.Measurements,
            m => m.Name == MeasurementNames.TimeToFirstSemanticEvent
                || m.Name == MeasurementNames.TimeToFirstClientEvent
                || m.Name == MeasurementNames.PersistenceDuration);
    }

    [Fact]
    public async Task Response_headers_are_stamped_when_they_arrived_not_when_the_body_finished()
    {
        // The runtime flushes its headers immediately and then stalls before the body. If the
        // boundary were stamped when the provider returned, both durations would be the stall, and
        // "time to response headers" would silently be reporting time to the whole response — the
        // single number an operator uses to tell "the runtime is slow to start" from "the runtime is
        // slow to generate".
        var stall = TimeSpan.FromMilliseconds(400);

        var record = await ProxyAsync(script: new UpstreamResponseScript
        {
            StatusCode = 200,
            ContentType = "application/json",
            Chunks = [new UpstreamChunk(Encoding.UTF8.GetBytes(Completion), stall)],
        });

        var opened = TimestampOf(record, ObservationType.UpstreamRequestOpened);
        var headers = TimestampOf(record, ObservationType.UpstreamHeadersReceived);
        var completed = TimestampOf(record, ObservationType.UpstreamCompleted);

        Assert.True(
            headers - opened < stall,
            FormattableString.Invariant($"Headers took {headers - opened}, which is not less than the {stall} body stall."));

        Assert.True(
            completed - opened >= stall,
            FormattableString.Invariant($"The upstream call took {completed - opened}, which is less than the {stall} body stall."));
    }

    [Fact]
    public async Task Establishing_a_connection_is_measured_separately_from_waiting_for_headers()
    {
        // Without this phase, a runtime slow to accept connections and a runtime slow to think are
        // the same number, and they send an operator to entirely different places
        // (docs/OBSERVABILITY.md "Latency phases").
        var record = await ProxyAsync();

        var connect = Assert.Single(
            record.Measurements,
            m => m.Name == MeasurementNames.UpstreamConnectDuration);

        Assert.Equal(MeasurementProvenance.Measured, connect.Provenance);
        Assert.Equal(MeasurementUnit.Milliseconds, connect.Unit);

        Assert.True(
            TimestampOf(record, ObservationType.UpstreamConnectionStarted)
            <= TimestampOf(record, ObservationType.UpstreamConnectionEstablished));

        Assert.True(
            TimestampOf(record, ObservationType.UpstreamConnectionEstablished)
            <= TimestampOf(record, ObservationType.UpstreamHeadersReceived));
    }

    [Fact]
    public async Task A_request_served_by_a_pooled_connection_records_no_connection_at_all()
    {
        // The honest half of the previous test. A reused connection establishes nothing, and a zero
        // here would claim a connection was opened instantaneously — which is a measurement of an
        // event that never happened (FR-TRACE-006).
        var records = await ProxyTwiceAsync();

        Assert.Contains(
            records[0].Measurements,
            m => m.Name == MeasurementNames.UpstreamConnectDuration);

        AssertAbsent(
            records[1],
            ObservationType.UpstreamConnectionStarted,
            ObservationType.UpstreamConnectionEstablished);

        Assert.DoesNotContain(
            records[1].Measurements,
            m => m.Name == MeasurementNames.UpstreamConnectDuration);
    }

    [Fact]
    public async Task Accepting_the_request_is_stamped_before_the_body_was_read()
    {
        // Both boundaries used to be taken in the application, after the read had already happened,
        // which made the read invisible and folded it into validation.
        var record = await ProxyAsync();

        Assert.True(
            TimestampOf(record, ObservationType.RequestAccepted)
            <= TimestampOf(record, ObservationType.RequestBodyRead));
    }

    [Fact]
    public async Task Every_observation_detail_is_bounded()
    {
        var record = await ProxyAsync();

        Assert.All(
            record.Observations,
            observation => Assert.True(observation.Details.Values.Count <= SafeDetails.MaxEntries));
    }

    /// <summary>Proxies twice through one gateway, so the second request meets a warm pool.</summary>
    private static async Task<IReadOnlyList<ExchangeRecord>> ProxyTwiceAsync()
    {
        var sink = new RecordingExchangeSink();

        await using var fixture = await GatewayFixture.StartAsync(
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
                settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
            },
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        for (var attempt = 0; attempt < 2; attempt++)
        {
            fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

            using var content = new StringContent(
                """{"model":"local-coder","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json");

            using var response = await fixture.Client.PostAsync(
                new Uri("/v1/chat/completions", UriKind.Relative),
                content);

            response.EnsureSuccessStatusCode();
        }

        Assert.Equal(2, sink.Records.Count);

        return sink.Records;
    }

    private static DateTimeOffset TimestampOf(ExchangeRecord record, ObservationType type) =>
        record.Observations.Single(observation => observation.Type == type).Timestamp;

    private static void AssertAbsent(ExchangeRecord record, params ObservationType[] types)
    {
        foreach (var type in types)
        {
            Assert.DoesNotContain(type, record.Observations.Select(observation => observation.Type));
        }
    }

    private static async Task<ExchangeRecord> ProxyAsync(
        Action<Dictionary<string, string?>>? configure = null,
        string model = "local-coder",
        string? body = null,
        UpstreamResponseScript? script = null)
    {
        var sink = new RecordingExchangeSink();

        await using var fixture = await GatewayFixture.StartAsync(
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
                settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
                configure?.Invoke(settings);
            },
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            script ?? UpstreamResponseScripts.Json(Completion));

        using var content = new StringContent(
            body ?? $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");

        using var response = await fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            content);

        response.EnsureSuccessStatusCode();

        return Assert.Single(sink.Records);
    }

    /// <summary>Captures the evidence the gateway hands to its sink.</summary>
    /// <remarks>
    /// The only way Stage 1A's timeline is observable: nothing is persisted and there is no
    /// administrative API yet, so without this seam the exit criterion "routing changes are
    /// represented as events" could not be verified at all.
    /// </remarks>
    private sealed class RecordingExchangeSink : IExchangeRecordSink
    {
        private readonly List<ExchangeRecord> records = [];

        internal IReadOnlyList<ExchangeRecord> Records
        {
            get
            {
                lock (records)
                {
                    return records.ToArray();
                }
            }
        }

        public ValueTask RecordAsync(ExchangeRecord record, CancellationToken cancellationToken)
        {
            lock (records)
            {
                records.Add(record);
            }

            return ValueTask.CompletedTask;
        }
    }
}
