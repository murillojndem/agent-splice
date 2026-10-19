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
/// Which budget bounds which wait on a stream: the first byte's until the runtime starts its body,
/// then the idle one's between reads (docs/SPECIFICATION.md FR-CHAT-006, FR-CHAT-007; ADR 0028).
/// </summary>
/// <remarks>
/// Real budgets against a real listener, because timeout phases are properties of the transport
/// (docs/TESTING.md). A wait that a budget must not cut is at most a fifth of that budget, and a
/// wait that a defect would cut runs well past the budget the defect would apply, so a slow machine
/// can only make a defect harder to see, never make correct code fail. Outcomes are asserted as a
/// status, a presence, or an absence — never as a duration.
/// </remarks>
public sealed class ChatCompletionStreamBudgetTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private const string ContentChunk = """{"choices":[{"index":0,"delta":{"content":"hello"}}]}""";

    [Fact]
    public async Task A_stream_that_waits_longer_than_the_idle_budget_for_its_first_byte_completes()
    {
        // The first trace report's case: headers at once, then a long wait while the runtime queues
        // the request and processes its prompt. A first read bounded by the idle budget cut it at
        // that budget; it has its own now, and the stream completes (ADR 0028).
        var gate = new UpstreamGate();
        var sink = new CapturingExchangeSink();
        var script = SseScript.Create().Gate(gate).Data(ContentChunk).Done();

        await using var fixture = await StartAsync(
            settings => GatewayFixture.SetTimeouts(
                settings,
                connect: "00:00:05",
                responseHeaders: "00:00:30",
                firstByte: "00:00:30",
                idleStream: "00:00:00.200",
                total: "00:00:30"),
            sink);

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        // Not awaited: the client's status is committed with the runtime's first byte (ADR 0025), so
        // no response can arrive until the gate opens.
        var pending = SendAsync(fixture);

        await gate.WaitForReachedAsync(WaitBudget);

        // Five idle budgets with nothing after the headers.
        await Task.Delay(TimeSpan.FromSeconds(1));
        gate.Release();

        using var response = await pending;

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.DoesNotContain(record.Observations, observation => observation.Type == ObservationType.TimeoutFired);
    }

    [Fact]
    public async Task A_stream_longer_than_the_idle_budget_completes_when_no_gap_between_reads_reaches_it()
    {
        // The idle budget measures the gap between reads, not the life of the stream: eight gaps of
        // 200 ms make a stream longer than its one-second budget, and none of them reaches it. A
        // budget armed once, at the first byte, would end this stream in the middle.
        var sink = new CapturingExchangeSink();
        var script = SseScript.Create().Data(ContentChunk);

        for (var gap = 0; gap < 8; gap++)
        {
            script.Data(ContentChunk, TimeSpan.FromMilliseconds(200));
        }

        script.Done();

        await using var fixture = await StartAsync(
            settings => GatewayFixture.SetTimeouts(
                settings,
                connect: "00:00:05",
                responseHeaders: "00:00:30",
                firstByte: "00:00:30",
                idleStream: "00:00:01",
                total: "00:00:30"),
            sink);

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await SendAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.DoesNotContain(record.Observations, observation => observation.Type == ObservationType.TimeoutFired);
    }

    [Fact]
    public async Task A_client_that_leaves_while_a_stream_waits_for_its_first_byte_cancels_the_upstream_request()
    {
        // Until the first byte, a read waits on the first-byte budget alone. Unless that budget is
        // linked to the client, a client that leaves during the wait leaves the runtime processing
        // its prompt for nobody, for as long as the budget allows (FR-CHAT-006).
        var gate = new UpstreamGate();
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(configure: null, sink);

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Gate(gate).Data(ContentChunk).Build());

        using var cancellation = new CancellationTokenSource();
        var pending = SendAsync(fixture, cancellation.Token);

        await gate.WaitForReachedAsync(WaitBudget);
        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);

        // Long enough for the gateway to hold the headers and be waiting for the first byte; the
        // record below says whether it was, so a cancellation that landed earlier fails the test
        // rather than passing it for the wrong reason.
        await Task.Delay(TimeSpan.FromMilliseconds(300));
        await cancellation.CancelAsync();

        // First, and bounded: a budget that outlived the client would hold the runtime's request, and
        // with it the gateway's request, for the whole five-minute budget.
        await recorded.WaitForAbortAsync(WaitBudget);
        await IgnoreClientOutcomeAsync(pending);

        var record = await sink.WaitForRecordAsync(WaitBudget);
        var types = record.Observations.Select(observation => observation.Type).ToList();

        Assert.Equal(ExchangeStatus.Cancelled, record.Exchange!.Status);
        Assert.Equal(FailureClass.RequestCancelled, record.Exchange.FailureClass);
        Assert.Contains(ObservationType.UpstreamHeadersReceived, types);
        Assert.DoesNotContain(ObservationType.TimeoutFired, types);
    }

    [Fact]
    public async Task A_stream_whose_headers_and_first_byte_each_take_most_of_their_budgets_completes()
    {
        // Each wait has its own budget: the header budget ends with the headers, and the first-byte
        // budget starts with them. One budget from the request to the first byte would have to hold
        // both waits, and here their sum is past either.
        var sink = new CapturingExchangeSink();
        var script = SseScript.Create()
            .WithHeaderDelay(TimeSpan.FromMilliseconds(800))
            .Data(ContentChunk, TimeSpan.FromMilliseconds(800))
            .Done();

        await using var fixture = await StartAsync(
            settings => GatewayFixture.SetTimeouts(
                settings,
                connect: "00:00:05",
                responseHeaders: "00:00:01.200",
                firstByte: "00:00:01.200",
                idleStream: "00:00:30",
                total: "00:00:30"),
            sink);

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await SendAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.DoesNotContain(record.Observations, observation => observation.Type == ObservationType.TimeoutFired);
    }

    /// <summary>Waits out a call whose client has already gone, whatever it reports.</summary>
    /// <remarks>
    /// The gateway writes nothing to a client that left, and the in-process host may complete that
    /// unwritten response before the client's own cancellation is seen. Which of the two the call then
    /// reports is the test host's race, not the gateway's behaviour, so neither is asserted.
    /// </remarks>
    private static async Task IgnoreClientOutcomeAsync(Task<HttpResponseMessage> pending)
    {
        try
        {
            (await pending.WaitAsync(WaitBudget)).Dispose();
        }
        catch (Exception exception)
            when (exception is OperationCanceledException or HttpRequestException or IOException)
        {
            // The usual outcome for a client that cancelled its own request.
        }
    }

    private static Task<GatewayFixture> StartAsync(
        Action<Dictionary<string, string?>>? configure,
        CapturingExchangeSink sink) =>
        GatewayFixture.StartAsync(
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
                settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
                configure?.Invoke(settings);
            },
            services => services.AddSingleton<IExchangeRecordSink>(sink));

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
}
