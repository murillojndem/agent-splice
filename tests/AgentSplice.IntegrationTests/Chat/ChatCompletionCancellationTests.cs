using System.Text;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// Client cancellation and its propagation to the runtime (FR-CHAT-006).
/// </summary>
/// <remarks>
/// The load-bearing assertion is <see cref="RecordedUpstreamRequest.WaitForAbortAsync"/>. Anything
/// less only proves that AgentSplice stopped reading, which a client experiences identically while
/// the runtime keeps generating — burning exactly the local compute that cancellation exists to
/// reclaim.
/// </remarks>
public sealed class ChatCompletionCancellationTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_client_disconnect_aborts_the_upstream_request()
    {
        await using var fixture = await StartAsync();

        // Headers arrive, then the runtime stalls, so the exchange is reliably in flight when the
        // client goes away.
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(30)));

        using var cancellation = new CancellationTokenSource();
        var request = PostAsync(fixture, cancellation.Token);

        var recorded = await fixture.Upstream.WaitForRequestAsync(WaitBudget);
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
        await recorded.WaitForAbortAsync(WaitBudget);

        Assert.True(recorded.WasAborted);
    }

    [Fact]
    public async Task A_stream_abandoned_between_reads_closes_the_runtimes_connection_at_once()
    {
        // A read in flight is cancelled at once. The case that waited was an answer given up between
        // reads — a client gone on a write, a bound crossed — whose body is disposed with nothing pending:
        // the handler read up to 1 MiB more for two seconds to keep the connection, while the runtime
        // generated for nobody. Exercised at the provider, where it happens, because the in-process host
        // always has a read pending when its client leaves.
        await using var fixture = await StartAsync();

        var script = SseScript.Create();

        for (var index = 0; index < 400; index++)
        {
            script.Data("""{"choices":[{"index":0,"delta":{"content":"token "}}]}""", TimeSpan.FromMilliseconds(10));
        }

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Done().Build());

        var target = fixture.Services.GetRequiredService<RuntimeRegistry>().Find(RuntimeEndpointId.Create(GatewayFixture.RuntimeId))!;
        var provider = fixture.Services.GetServices<IModelRuntimeProvider>().Single(candidate => candidate.ProviderKey == "lmstudio");

        var result = await provider.StreamAsync(
            ProviderCompletionRequest.Create(
                target,
                Encoding.UTF8.GetBytes("""{"model":"m","stream":true,"messages":[{"role":"user","content":"hi"}]}"""),
                "application/json",
                "text/event-stream",
                PublicRequestId.New()),
            CancellationToken.None);

        Assert.True(result.Opened);

        await result.Body!.ReadAsync(new byte[4096], CancellationToken.None);

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);

        await result.Body.DisposeAsync();

        // Half the drain the handler used to spend.
        await recorded.WaitForAbortAsync(TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task A_client_gone_while_a_buffered_answer_is_written_is_recorded_as_cancelled()
    {
        // The buffered path recorded an exchange before writing a byte, so a client gone during the
        // write was recorded as completed with the whole body "delivered". It is recorded after the
        // write now. The write is scripted, because over a socket whether a client can still be seen
        // leaving depends on how much of the answer the server buffers for it: on Windows and on CI's
        // Linux runners Kestrel takes all of a 32 MiB answer within milliseconds, so a test that hung up
        // a real socket passed only when its reset beat the copy.
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.Json("""{"id":"c1","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}"""));

        var gateway = fixture.Services.GetRequiredService<ChatCompletionGateway>();
        var client = new GoneAtTheBodySink();
        var now = DateTimeOffset.UtcNow;

        await gateway.CompleteAsync(
            ChatCompletionRequest.Create(
                Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"hi"}]}"""),
                PublicRequestId.New(),
                now,
                now),
            client,
            CancellationToken.None);

        var record = await sink.WaitForRecordAsync(WaitBudget);
        var types = record.Observations.Select(observation => observation.Type).ToList();

        Assert.True(client.BodyWasOffered);
        Assert.Equal(ExchangeStatus.Cancelled, record.Exchange!.Status);
        Assert.Contains(ObservationType.ClientResponseStarted, types);
        Assert.Contains(ObservationType.ClientCancelled, types);
        Assert.DoesNotContain(ObservationType.ClientCompleted, types);
        Assert.DoesNotContain(record.Measurements, measurement => measurement.Name == MeasurementNames.ClientResponseBytes);
    }

    [Fact]
    public async Task A_client_disconnect_is_recorded_as_a_cancellation_rather_than_a_timeout()
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(services =>
            services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(30)));

        using var cancellation = new CancellationTokenSource();
        var request = PostAsync(fixture, cancellation.Token);

        await fixture.Upstream.WaitForRequestAsync(WaitBudget);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(ExchangeStatus.Cancelled, record.Exchange?.Status);
        Assert.Equal(FailureClass.RequestCancelled, record.Exchange?.FailureClass);
    }

    [Fact]
    public async Task A_cancelled_exchange_records_the_client_as_the_source()
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(services =>
            services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(30)));

        using var cancellation = new CancellationTokenSource();
        var request = PostAsync(fixture, cancellation.Token);

        await fixture.Upstream.WaitForRequestAsync(WaitBudget);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var record = await sink.WaitForRecordAsync(WaitBudget);
        var cancelled = record.Observations.Single(o => o.Type == ObservationType.ClientCancelled);

        Assert.Equal("client", cancelled.Details.Values["cancellation.source"]);
    }

    [Fact]
    public async Task A_cancelled_exchange_is_still_handed_to_the_record_sink()
    {
        // The evidence for a cancelled exchange is exactly the evidence worth keeping, so recording
        // must not itself be cancelled along with the request (FR-DATA-009).
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(services =>
            services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(30)));

        using var cancellation = new CancellationTokenSource();
        var request = PostAsync(fixture, cancellation.Token);

        await fixture.Upstream.WaitForRequestAsync(WaitBudget);
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.NotEmpty(record.Observations);
        Assert.Contains(
            ObservationType.UpstreamRequestOpened,
            record.Observations.Select(observation => observation.Type));
    }

    [Fact]
    public async Task A_disconnect_before_forwarding_opens_no_upstream_request()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await using var fixture = await StartAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => PostAsync(fixture, cancellation.Token));

        Assert.Empty(fixture.Upstream.ReceivedRequests);
    }

    private static Task<GatewayFixture> StartAsync(Action<IServiceCollection>? configureServices = null) =>
        GatewayFixture.StartAsync(
            settings =>
            {
                settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";

                // Generous, so the test provokes a disconnect rather than racing a timeout.
                GatewayFixture.SetTimeouts(
                    settings,
                    connect: "00:00:05",
                    responseHeaders: "00:01:00",
                    firstByte: "00:01:00",
                    idleStream: "00:01:00",
                    total: "00:05:00");
            },
            configureServices);

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture fixture, CancellationToken cancellationToken)
    {
        var content = new StringContent(
            """{"model":"m","messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");

        return fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            content,
            cancellationToken);
    }

    /// <summary>A client that took the response's status and was gone by the time its body was written.</summary>
    private sealed class GoneAtTheBodySink : IClientResponseSink
    {
        public bool HasStarted { get; private set; }

        public bool BodyWasOffered { get; private set; }

        public ValueTask<ClientWriteResult> StartAsync(ClientResponseStart start, CancellationToken cancellationToken)
        {
            HasStarted = true;
            return ValueTask.FromResult(ClientWriteResult.Written);
        }

        public ValueTask<ClientWriteResult> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            BodyWasOffered = true;
            return ValueTask.FromResult(ClientWriteResult.ClientGone);
        }

        public void Abort()
        {
        }
    }
}
