using System.Net;
using System.Text;
using System.Text.Json;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Observations;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// Every way a streamed exchange can end badly, and what each one is recorded as
/// (docs/SPECIFICATION.md FR-STR-007, FR-STR-011, FR-CHAT-006, FR-CHAT-007).
/// </summary>
/// <remarks>
/// The distinctions here are the entire point of the termination vocabulary. From the outside a
/// client disconnect, a runtime that stalled, a runtime that reset, and a gateway bound all look
/// identical: the stream stopped. A trace that collapses them tells an operator nothing they could
/// not already see.
///
/// Where the response has already started, each test also asserts that the client's read <em>fails</em>
/// rather than ending cleanly. A truncated event stream closed politely is indistinguishable from a
/// complete one for any client that does not require a terminator, so ending politely would turn a
/// failure into an apparent success.
/// </remarks>
public sealed class ChatCompletionStreamFailureTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private const string ContentChunk = """{"choices":[{"index":0,"delta":{"content":"hello"}}]}""";

    [Fact]
    public async Task A_client_disconnect_mid_stream_aborts_the_upstream_request()
    {
        // FR-CHAT-006. Anything less only shows that AgentSplice stopped reading, which a client
        // experiences identically while the runtime keeps generating and burning the compute
        // cancellation exists to reclaim.
        var gate = new UpstreamGate();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Gate(gate).Data(ContentChunk).Build());

        var response = await SendAsync(fixture);

        // Already recorded: the response headers cannot have reached the client before the upstream
        // request reached the fixture.
        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);

        // Read the first event, so the disconnect happens in the middle of a live stream rather
        // than before one started.
        using (var reader = new SseClientReader(await response.Content.ReadAsStreamAsync()))
        {
            Assert.NotNull(await reader.ReadEventAsync());
        }

        await gate.WaitForReachedAsync(WaitBudget);

        // The client hangs up. Anything less than the upstream's own abort signal only shows that
        // AgentSplice stopped reading, which a client experiences identically while the runtime
        // keeps generating and burning the compute cancellation exists to reclaim.
        response.Dispose();

        await recorded.WaitForAbortAsync(WaitBudget);
    }

    [Fact]
    public async Task A_client_disconnect_mid_stream_is_recorded_as_a_client_cancellation()
    {
        var gate = new UpstreamGate();
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(
            configure: null,
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Gate(gate).Data(ContentChunk).Build());

        var response = await SendAsync(fixture);

        using (var reader = new SseClientReader(await response.Content.ReadAsStreamAsync()))
        {
            Assert.NotNull(await reader.ReadEventAsync());
        }

        await gate.WaitForReachedAsync(WaitBudget);

        response.Dispose();

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(ExchangeStatus.Cancelled, record.Exchange!.Status);
        Assert.Equal(StreamTermination.ClientCancelled, record.Exchange.StreamTermination);
        Assert.Equal(FailureClass.RequestCancelled, record.Exchange.FailureClass);
        Assert.True(record.Exchange.StreamedResponse);
    }

    [Fact]
    public async Task An_upstream_reset_mid_stream_is_recorded_as_a_lost_connection()
    {
        // Gated so the client is demonstrably idle when the runtime resets. Without that, a client
        // whose own read fails first can make its disconnect the earlier event, and the exchange
        // would honestly — but uninterestingly — be recorded as a cancellation instead.
        var gate = new UpstreamGate();
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(
            configure: null,
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Gate(gate).ClosePrematurely().Build());

        using var response = await SendAsync(fixture);

        using (var reader = new SseClientReader(await response.Content.ReadAsStreamAsync()))
        {
            Assert.NotNull(await reader.ReadEventAsync());

            await gate.WaitForReachedAsync(WaitBudget);
            gate.Release();

            var record = await sink.WaitForRecordAsync(WaitBudget);

            Assert.Equal(ExchangeStatus.Failed, record.Exchange!.Status);
            Assert.Equal(StreamTermination.ConnectionLost, record.Exchange.StreamTermination);
            Assert.Equal(FailureClass.InvalidUpstreamResponse, record.Exchange.FailureClass);
        }
    }

    [Fact]
    public async Task An_upstream_reset_mid_stream_does_not_end_the_client_transfer_cleanly()
    {
        // Gated so the reset lands while the client is blocked waiting for more, rather than while
        // it still has buffered bytes to hand back. Ungated, the client can finish reading what
        // already arrived and see a clean end before the abort reaches it — which made this test
        // report success for a truncated stream perhaps one run in four.
        var gate = new UpstreamGate();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Gate(gate).ClosePrematurely().Build());

        using var response = await SendAsync(fixture);
        using var stream = await response.Content.ReadAsStreamAsync();

        using (var reader = new SseClientReader(stream))
        {
            Assert.NotNull(await reader.ReadEventAsync());

            await gate.WaitForReachedAsync(WaitBudget);
            gate.Release();

            // The claim: a client must not be able to mistake a truncated stream for a whole one.
            await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadToEndAsync());
        }
    }

    [Fact]
    public async Task A_stalled_stream_fires_the_idle_phase_and_not_the_response_header_phase()
    {
        // The header budget is deliberately shorter than the stream. Left armed after headers
        // arrive, its token is signalled during every long stream, and a classifier that consulted
        // it would report each mid-stream stall as a runtime that was slow to answer — pointing an
        // operator at prompt processing when the problem is generation.
        var gate = new UpstreamGate();

        var record = await StreamAndRecordAsync(
            SseScript.Create().Data(ContentChunk).Gate(gate).Data(ContentChunk).Build(),
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:00:00.300";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:00.500";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:30";
            });

        Assert.Equal(StreamTermination.Timeout, record.Exchange!.StreamTermination);
        Assert.Equal(FailureClass.UpstreamTimeout, record.Exchange.FailureClass);

        var timeout = record.Observations.Single(o => o.Type == ObservationType.TimeoutFired);

        Assert.Equal(nameof(TimeoutPhaseNames.IdleStream), timeout.Details.Values["timeout.phase"]);
    }

    [Fact]
    public async Task A_runtime_that_never_sends_headers_still_fails_before_the_response_starts()
    {
        // Nothing has been committed yet, so this failure can still be expressed as a status the
        // client can act on rather than as an abandoned stream.
        await using var fixture = await StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:00:00.300";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:10";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:30";
        });

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallBeforeHeaders(TimeSpan.FromSeconds(20)));

        using var response = await SendAsync(fixture);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.Equal("agentsplice_upstream_timeout", await ErrorCodeAsync(response));
    }

    [Fact]
    public async Task An_event_larger_than_its_bound_ends_the_stream_and_is_not_blamed_on_the_runtime()
    {
        // A gateway policy stop, recorded as one. Reporting it as a malformed event would blame the
        // runtime for a decision AgentSplice made about its own memory.
        var record = await StreamAndRecordAsync(
            SseScript.Create().Raw("data: " + new string('x', 4096)).Build(),
            settings => settings["agentsplice:limits:maxStreamEventBytes"] = "256");

        Assert.Equal(StreamTermination.LimitExceeded, record.Exchange!.StreamTermination);
        Assert.Equal(FailureClass.InvalidUpstreamStream, record.Exchange.FailureClass);
    }

    [Fact]
    public async Task An_event_larger_than_its_bound_does_not_end_the_client_transfer_cleanly()
    {
        await using var fixture = await StartAsync(settings =>
            settings["agentsplice:limits:maxStreamEventBytes"] = "256");

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Raw("data: " + new string('x', 4096)).Build());

        Assert.False(await CompletesCleanlyAsync(fixture));
    }

    [Fact]
    public async Task A_malformed_event_payload_is_relayed_and_recorded_rather_than_repaired()
    {
        // The client's own parser is the authority on the runtime's protocol. Substituting a gateway
        // error would discard the most actionable diagnostic a user has, which is the same reasoning
        // that relays a runtime's `429 text/plain` verbatim.
        var script = SseScript.Create().Data("{not json").Data(ContentChunk).Done();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await SendAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_malformed_event_payload_completes_the_exchange_with_no_failure_class()
    {
        var record = await StreamAndRecordAsync(
            SseScript.Create().Data("{not json").Done().Build());

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.Equal(StreamTermination.MalformedEvent, record.Exchange.StreamTermination);
        Assert.Null(record.Exchange.FailureClass);

        // The tidy ending survives even though the termination names the anomaly.
        var completed = record.Observations.Single(o => o.Type == ObservationType.ClientCompleted);

        Assert.Equal("observed", completed.Details.Values["stream.terminator"]);
    }

    [Fact]
    public async Task A_stream_that_ends_mid_event_records_how_much_of_it_never_arrived()
    {
        var record = await StreamAndRecordAsync(
            SseScript.Create().Data(ContentChunk).Raw("data: {\"choices\":").Build());

        Assert.Equal(StreamTermination.MalformedEvent, record.Exchange!.StreamTermination);

        var completed = record.Observations.Single(o => o.Type == ObservationType.ClientCompleted);

        Assert.Equal("absent", completed.Details.Values["stream.terminator"]);
        Assert.Equal("17", completed.Details.Values["stream.incomplete_event_bytes"]);
    }

    [Fact]
    public async Task An_upstream_error_status_on_a_streaming_request_is_relayed_verbatim()
    {
        const string Body = """{"error":{"message":"context length exceeded"}}""";

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Body, 400));

        using var response = await SendAsync(fixture);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(Body, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_upstream_authentication_failure_on_a_streaming_request_is_never_echoed()
    {
        // The credential is the gateway's, not the client's, so echoing 401 would tell a client to
        // fix a key it does not own, and the body can hint at that key's shape.
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.Text("""{"error":"bad key sk-abc"}""", "application/json", 401));

        using var response = await SendAsync(fixture);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal("agentsplice_runtime_authentication_failed", await ErrorCodeAsync(response));
        Assert.DoesNotContain("sk-abc", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_buffered_answer_to_a_streaming_request_is_relayed_and_recorded_as_not_streamed()
    {
        // A runtime that ignores the flag has not failed. It is answering, and the answer reaches
        // the client unchanged — but the exchange says plainly that no stream was served, because
        // nothing else in the record would show it.
        const string Completion =
            """{"id":"c1","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}],"usage":{"prompt_tokens":4,"completion_tokens":2}}""";

        var record = await StreamAndRecordAsync(UpstreamResponseScripts.Json(Completion));

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.False(record.Exchange.StreamedResponse);
        Assert.Equal(StreamTermination.NotApplicable, record.Exchange.StreamTermination);

        var headers = record.Observations.Single(o => o.Type == ObservationType.UpstreamHeadersReceived);

        Assert.Equal("false", headers.Details.Values["upstream.streamed"]);

        // Asking to stream must not cost evidence: the buffered answer is summarised exactly as it
        // would have been without the flag.
        Assert.Equal(4, record.Exchange.Usage.PromptTokens?.Value);
        Assert.Equal(["stop"], record.Exchange.ResponseSummary?.FinishReasons);
    }

    /// <summary>Sends a streaming request and reports whether the whole exchange ended cleanly.</summary>
    /// <remarks>
    /// Covers the send as well as the read, because an abort can surface at either point depending
    /// on how far the transport had got. Both are the same fact to a client: the answer it holds is
    /// not the whole answer.
    /// </remarks>
    private static async Task<bool> CompletesCleanlyAsync(GatewayFixture fixture)
    {
        try
        {
            using var response = await SendAsync(fixture);

            await response.Content.ReadAsByteArrayAsync();

            return true;
        }
        catch (Exception exception)
            when (exception is HttpRequestException or IOException or OperationCanceledException)
        {
            return false;
        }
    }

    private static async Task<bool> ReadsToACleanEndAsync(HttpResponseMessage response)
    {
        try
        {
            await response.Content.ReadAsByteArrayAsync();
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException)
        {
            return false;
        }
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    private static async Task<ExchangeRecord> StreamAndRecordAsync(
        UpstreamResponseScript script,
        Action<Dictionary<string, string?>>? configure = null)
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(
            configure,
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script);

        // The client's own outcome is not what these tests assert; the record is. An abandoned
        // response is one of the outcomes under test, so failing to read it is expected.
        await CompletesCleanlyAsync(fixture);

        return await sink.WaitForRecordAsync(WaitBudget);
    }

    private static Task<GatewayFixture> StartAsync(
        Action<Dictionary<string, string?>>? configure = null,
        Action<IServiceCollection>? configureServices = null) =>
        GatewayFixture.StartAsync(
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
                settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
                configure?.Invoke(settings);
            },
            configureServices);

    private static async Task<HttpResponseMessage> SendAsync(
        GatewayFixture fixture,
        CancellationToken cancellationToken = default)
    {
        using var content = new StringContent(
            """{"model":"local-coder","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = content,
        };

        return await fixture.Client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    /// <summary>Names the timeout phases this suite asserts on, without reaching into the application.</summary>
    private static class TimeoutPhaseNames
    {
        internal const string IdleStream = nameof(IdleStream);
    }
}
