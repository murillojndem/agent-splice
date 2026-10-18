using System.Net;
using System.Text;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Observations;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// What a failed exchange records about how it failed, on the paths that answer with an error
/// envelope (docs/SPECIFICATION.md FR-CHAT-008, FR-TRACE-005; ADR 0025).
/// </summary>
/// <remarks>
/// The provider attributes every timeout to a phase and every other failure to a reason, and until
/// ADR 0025 only a mid-stream timeout ever reached the timeline. A non-streamed request — the path
/// most LM Studio requests take, because LM Studio sends their headers with the finished answer —
/// recorded a 504 and threw the phase away, and a refused connection or a truncated body recorded
/// nothing at all.
/// </remarks>
public sealed class FailureEvidenceTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_runtime_that_never_sends_headers_records_the_response_header_phase()
    {
        var record = await FailAsync(
            UpstreamResponseScripts.StallBeforeHeaders(TimeSpan.FromSeconds(10)),
            HttpStatusCode.GatewayTimeout,
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:00:00.250";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:10";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:30";
            });

        Assert.Equal("ResponseHeaders", Phase(record));
        AssertDelivered(record, "agentsplice_upstream_timeout");
    }

    [Fact]
    public async Task A_runtime_that_answers_then_stalls_records_the_total_phase()
    {
        // Validation keeps every phase within the total, so the total is what elapses once headers
        // have arrived and the body read stalls.
        var record = await FailAsync(
            UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(10)),
            HttpStatusCode.GatewayTimeout,
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "timeouts:connect")] = "00:00:00.500";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:00:00.500";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:00.500";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:00.500";
            });

        Assert.Equal("Total", Phase(record));
        AssertDelivered(record, "agentsplice_upstream_timeout");
    }

    [Fact]
    public async Task A_refused_connection_records_the_socket_error_and_no_timeout()
    {
        var record = await FailAsync(
            script: null,
            HttpStatusCode.BadGateway,
            settings => settings[GatewayFixture.RuntimeKey(0, "baseUrl")] = "http://127.0.0.1:1/v1");

        var failed = Failed(record);

        Assert.Equal("Unreachable", failed.Details.Values["upstream.failure"]);
        Assert.Equal("ConnectionRefused", failed.Details.Values["upstream.socket_error"]);
        Assert.DoesNotContain(record.Observations, o => o.Type == ObservationType.TimeoutFired);
        AssertDelivered(record, "agentsplice_runtime_unavailable");
    }

    [Fact]
    public async Task A_body_the_runtime_never_finished_records_that_it_was_truncated()
    {
        var record = await FailAsync(UpstreamResponseScripts.UnderDeliveredContentLength(), HttpStatusCode.BadGateway);

        Assert.Equal("body.truncated", Failed(record).Details.Values["upstream.completion"]);
        AssertDelivered(record, "agentsplice_invalid_upstream_response");
    }

    [Fact]
    public async Task A_body_over_its_bound_records_that_it_was_too_large()
    {
        var record = await FailAsync(
            UpstreamResponseScripts.Json($$"""{"padding":"{{new string('y', 4096)}}"}"""),
            HttpStatusCode.BadGateway,
            settings => settings["agentsplice:limits:maxUpstreamCompletionBodyBytes"] = "1024");

        var failed = Failed(record);

        Assert.Equal("ResponseTooLarge", failed.Details.Values["upstream.failure"]);
        Assert.Equal("body.too_large", failed.Details.Values["upstream.completion"]);
    }

    [Fact]
    public async Task A_rejected_credential_records_the_rejection()
    {
        var record = await FailAsync(
            UpstreamResponseScripts.Text("""{"error":"bad key"}""", "application/json", 401),
            HttpStatusCode.BadGateway);

        Assert.Equal("AuthenticationRejected", Failed(record).Details.Values["upstream.failure"]);
        AssertDelivered(record, "agentsplice_runtime_authentication_failed");
    }

    [Fact]
    public async Task A_relayed_answer_records_no_failure_and_says_it_was_relayed()
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(configure: null, sink);

        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.DoesNotContain(record.Observations, o => o.Type is ObservationType.TimeoutFired or ObservationType.UpstreamFailed);
        Assert.Null(record.Error);
        Assert.Equal(
            "relayed",
            record.Observations.Single(o => o.Type == ObservationType.ClientCompleted).Details.Values["client.response"]);
    }

    [Fact]
    public async Task A_request_refused_before_its_model_was_known_records_how_its_envelope_reached_the_client()
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(configure: null, sink);

        using var response = await fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            new StringContent("not json", Encoding.UTF8, "application/json"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Null(record.Exchange);
        AssertDelivered(record, "agentsplice_invalid_request");

        // Its trace link is kept, although there is no exchange to carry it (ADR 0025).
        Assert.Equal(response.Headers.GetValues("x-agentsplice-trace-id").Single(), record.TraceId?.Value);
    }

    [Fact]
    public async Task A_client_that_leaves_while_its_model_is_discovered_is_a_cancellation_not_an_outage()
    {
        // The discovery the client abandoned says nothing about the runtime. Recorded as a failure it
        // blamed a runtime for a disconnect, as agentsplice_runtime_unavailable, with a 502 written to
        // nobody.
        var sink = new CapturingExchangeSink();

        await using var fixture = await GatewayFixture.StartAsync(
            settings => settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "true",
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.StallBeforeHeaders(TimeSpan.FromSeconds(30)));

        using var cancellation = new CancellationTokenSource();
        var request = fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            new StringContent(Minimal, Encoding.UTF8, "application/json"),
            cancellation.Token);

        var discovery = await fixture.Upstream.WaitForRequestAsync(WaitBudget);

        Assert.EndsWith("/models", discovery.Path, StringComparison.Ordinal);

        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(ExchangeStatus.Cancelled, record.Exchange!.Status);
        Assert.Equal(FailureClass.RequestCancelled, record.Exchange.FailureClass);
        Assert.Null(record.Error);
    }

    private const string Minimal = """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";

    private const string Completion =
        """{"id":"c1","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""";

    private static string Phase(ExchangeRecord record) =>
        record.Observations.Single(o => o.Type == ObservationType.TimeoutFired).Details.Values["timeout.phase"];

    private static ExchangeObservation Failed(ExchangeRecord record) =>
        record.Observations.Single(o => o.Type == ObservationType.UpstreamFailed);

    /// <summary>The envelope was written, and the record says the client received it.</summary>
    private static void AssertDelivered(ExchangeRecord record, string code)
    {
        Assert.Equal(code, record.Error?.Code);
        Assert.Equal(
            "gateway_error",
            record.Observations.Single(o => o.Type == ObservationType.ClientCompleted).Details.Values["client.response"]);

        // The commit precedes the completion, and both follow everything the upstream did.
        var started = record.Observations.Single(o => o.Type == ObservationType.ClientResponseStarted);
        var completed = record.Observations.Single(o => o.Type == ObservationType.ClientCompleted);

        Assert.True(started.Sequence < completed.Sequence);
        Assert.True(started.Timestamp <= completed.Timestamp);
    }

    private static async Task<ExchangeRecord> FailAsync(
        UpstreamResponseScript? script,
        HttpStatusCode expected,
        Action<Dictionary<string, string?>>? configure = null)
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(configure, sink);

        if (script is not null)
        {
            fixture.Upstream.EnqueueFor("/v1/chat/completions", script);
        }

        using var response = await PostAsync(fixture);

        Assert.Equal(expected, response.StatusCode);

        return await sink.WaitForRecordAsync(WaitBudget);
    }

    private static Task<GatewayFixture> StartAsync(
        Action<Dictionary<string, string?>>? configure,
        CapturingExchangeSink sink) =>
        GatewayFixture.StartAsync(
            settings =>
            {
                settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                configure?.Invoke(settings);
            },
            services => services.AddSingleton<IExchangeRecordSink>(sink));

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture fixture) =>
        fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            new StringContent(Minimal, Encoding.UTF8, "application/json"));
}
