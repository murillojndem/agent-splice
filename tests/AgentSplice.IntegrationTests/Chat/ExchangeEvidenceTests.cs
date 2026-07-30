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
    public async Task Every_observation_detail_is_bounded()
    {
        var record = await ProxyAsync();

        Assert.All(
            record.Observations,
            observation => Assert.True(observation.Details.Values.Count <= SafeDetails.MaxEntries));
    }

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
        string? body = null)
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

        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

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
