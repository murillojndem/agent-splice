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
                settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:01:00";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:01:00";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:05:00";
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

    /// <summary>Captures records and lets a test wait for one to arrive.</summary>
    /// <remarks>
    /// Recording happens after the client has already been released, so the test has to wait for it
    /// rather than assume it has finished.
    /// </remarks>
    private sealed class CapturingExchangeSink : IExchangeRecordSink
    {
        private readonly TaskCompletionSource<ExchangeRecord> first =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ValueTask RecordAsync(ExchangeRecord record, CancellationToken cancellationToken)
        {
            first.TrySetResult(record);
            return ValueTask.CompletedTask;
        }

        internal async Task<ExchangeRecord> WaitForRecordAsync(TimeSpan timeout)
        {
            var completed = await Task.WhenAny(first.Task, Task.Delay(timeout)).ConfigureAwait(false);

            if (completed != first.Task)
            {
                throw new TimeoutException("No exchange record was handed to the sink in time.");
            }

            return await first.Task.ConfigureAwait(false);
        }
    }
}
