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
/// What a client receives on the wire from the gateway hosted the way it ships: on Kestrel, over a
/// real socket on the IPv4 loopback (ADR 0025).
/// </summary>
/// <remarks>
/// The rest of the suite hosts the gateway in memory, where a response's headers leave as soon as
/// they are committed. Kestrel sends them only with the first body write, so a stream committed at
/// the runtime's headers and abandoned before its first byte reached a real client as a bare reset
/// — and no in-memory test could see it (ADR 0019). These tests are the ones that can, and each was
/// run against the code before ADR 0025 to watch it fail.
/// </remarks>
public sealed class KestrelWireTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private const string ContentChunk = """{"choices":[{"index":0,"delta":{"content":"hello"}}]}""";

    [Fact]
    public async Task A_stream_that_stalls_before_its_first_byte_gets_a_status_and_its_correlation_headers()
    {
        // The case the first trace report met: LM Studio sends a stream's headers at once and its
        // first byte after the prompt is processed, and the idle budget elapses in between. The
        // client used to receive nothing — no status line, no header — and then a reset.
        await using var fixture = await StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:00.500";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:30";
        });

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(20)));

        using var response = await SendStreamingAsync(fixture);

        Assert.Equal(HttpStatusCode.GatewayTimeout, response.StatusCode);
        Assert.True(response.Headers.Contains("x-agentsplice-exchange-id"));
        Assert.True(response.Headers.Contains("x-agentsplice-request-id"));
        Assert.Contains("agentsplice_upstream_timeout", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_runtime_that_drops_before_its_first_byte_gets_a_bad_gateway()
    {
        await using var fixture = await StartAsync();

        fixture.Upstream.EnqueueFor("/v1/chat/completions", ChatCompletionStreamFailureTests.HeadersThenReset());

        using var response = await SendStreamingAsync(fixture);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(response.Headers.Contains("x-agentsplice-exchange-id"));
        Assert.Contains("agentsplice_invalid_upstream_response", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_successful_stream_reaches_the_client_byte_for_byte()
    {
        // Committing at the first byte changes nothing for a stream that succeeds: Kestrel already
        // sent its headers with that byte.
        var script = SseScript.Create().Data(ContentChunk).Data(ContentChunk).Done();

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await SendStreamingAsync(fixture);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/event-stream", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.Contains("x-agentsplice-exchange-id"));
        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task A_stream_that_stalls_mid_answer_is_still_abandoned_rather_than_ended_cleanly()
    {
        // After the first byte nothing changes: the response is abandoned, because a truncated
        // stream closed politely is indistinguishable from a whole one (ADR 0009 decision 5).
        var gate = new UpstreamGate();
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(
            settings =>
            {
                settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:00.500";
                settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:30";
            },
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data(ContentChunk).Gate(gate).Data(ContentChunk).Build());

        using var response = await SendStreamingAsync(fixture);
        using var reader = new SseClientReader(await response.Content.ReadAsStreamAsync());

        Assert.NotNull(await reader.ReadEventAsync());
        await Assert.ThrowsAnyAsync<Exception>(() => reader.ReadToEndAsync());

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(StreamTermination.Timeout, record.Exchange!.StreamTermination);
        Assert.Null(record.Error);
        Assert.Contains(record.Observations, o => o.Type == ObservationType.ClientResponseAbandoned);
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
            configureServices,
            kestrel: true);

    private static async Task<HttpResponseMessage> SendStreamingAsync(GatewayFixture fixture)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(
                """{"model":"local-coder","stream":true,"messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"),
        };

        return await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
}
