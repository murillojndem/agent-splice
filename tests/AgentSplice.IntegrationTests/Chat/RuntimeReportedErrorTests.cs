using System.Net;
using System.Text;
using System.Text.Json;
using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Observations;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using AgentSplice.TestSupport.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// A runtime that reports an error inside a success, and what the exchange records of it (ADR 0026).
/// </summary>
/// <remarks>
/// The first trace report's finding 1: LM Studio, unable to render a chat template, answered
/// <c>200</c> with one event carrying an error object and closed the stream. AgentSplice relayed it
/// unchanged, which is right, and recorded it as <c>completed</c> with <c>normal_completion</c> —
/// exactly how a stream that worked is recorded. The exchange still completes, because AgentSplice did
/// not fail; what changes is that the record, the metrics, and the dashboard now say the runtime did.
/// </remarks>
public sealed class RuntimeReportedErrorTests
{
    private const string MessageSentinel = "SENTINEL-RUNTIME-ERROR-5d1e";

    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    private static readonly string ErrorObject =
        """{"error":{"message":"Error rendering prompt with jinja template: SENTINEL","type":"server_error"}}"""
            .Replace("SENTINEL", MessageSentinel, StringComparison.Ordinal);

    [Fact]
    public async Task A_stream_whose_runtime_reports_an_error_is_relayed_unchanged_and_recorded_as_the_runtimes_error()
    {
        var script = SseScript.Create().Data(ErrorObject);
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(sink);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", script.Build());

        using var response = await SendAsync(fixture, streaming: true);

        // The client's own parser is the authority on what the event means to it.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(script.ToBytes(), await response.Content.ReadAsByteArrayAsync());

        var record = await sink.WaitForRecordAsync(WaitBudget);
        var exchange = record.Exchange!;

        Assert.Equal(ExchangeStatus.Completed, exchange.Status);
        Assert.Equal(StreamTermination.UpstreamError, exchange.StreamTermination);
        Assert.Null(exchange.FailureClass);
        Assert.Null(record.Error);
        Assert.True(exchange.ResponseSummary?.UpstreamErrorReported);
        Assert.Equal(UpstreamOutcome.ErrorReported, exchange.UpstreamOutcome);
        Assert.Single(record.Observations, o => o.Type == ObservationType.UpstreamErrorReported);

        var completed = record.Observations.Single(o => o.Type == ObservationType.ClientCompleted);

        Assert.Equal("absent", completed.Details.Values["stream.terminator"]);
    }

    [Fact]
    public async Task An_error_followed_by_the_terminator_is_still_the_runtimes_error()
    {
        // With [DONE] after the error even the absent terminator, the one sign the record used to
        // carry, disappeared. It survives in the completion details either way.
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(sink);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", SseScript.Create().Data(ErrorObject).Done().Build());

        using var response = await SendAsync(fixture, streaming: true);
        await response.Content.ReadAsByteArrayAsync();

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(StreamTermination.UpstreamError, record.Exchange!.StreamTermination);
        Assert.Equal(
            "observed",
            record.Observations.Single(o => o.Type == ObservationType.ClientCompleted).Details.Values["stream.terminator"]);
    }

    [Fact]
    public async Task A_buffered_success_that_is_an_error_object_is_recorded_as_the_runtimes_error()
    {
        var sink = new CapturingExchangeSink();

        await using var fixture = await StartAsync(sink);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(ErrorObject));

        using var response = await SendAsync(fixture, streaming: false);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(ErrorObject, await response.Content.ReadAsStringAsync());

        var record = await sink.WaitForRecordAsync(WaitBudget);

        Assert.Equal(ExchangeStatus.Completed, record.Exchange!.Status);
        Assert.Equal(StreamTermination.NotApplicable, record.Exchange.StreamTermination);
        Assert.Equal(UpstreamOutcome.ErrorReported, record.Exchange.UpstreamOutcome);
        Assert.Single(record.Observations, o => o.Type == ObservationType.UpstreamErrorReported);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_runtimes_error_message_reaches_neither_the_record_nor_any_log(bool streaming)
    {
        // The message can echo a template or the prompt, so only the error's presence is recorded.
        var sink = new CapturingExchangeSink();
        var logs = new CapturingLoggerProvider();

        await using var fixture = await StartAsync(sink, logs);
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            streaming ? SseScript.Create().Data(ErrorObject).Build() : UpstreamResponseScripts.Json(ErrorObject));

        using var response = await SendAsync(fixture, streaming);
        await response.Content.ReadAsByteArrayAsync();

        var record = await sink.WaitForRecordAsync(WaitBudget);

        var recorded = string.Join(
            "\n",
            record.Observations.SelectMany(o => o.Details.Values.Values)
                .Append(JsonSerializer.Serialize(record.Exchange?.ResponseSummary)));

        Assert.DoesNotContain(MessageSentinel, recorded, StringComparison.Ordinal);
        Assert.DoesNotContain(logs.AllText, text => text.Contains(MessageSentinel, StringComparison.Ordinal));
        Assert.NotEmpty(logs.Records);
    }

    private static Task<GatewayFixture> StartAsync(CapturingExchangeSink sink, CapturingLoggerProvider? logs = null) =>
        GatewayFixture.StartAsync(
            settings =>
            {
                settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings["Logging:LogLevel:Default"] = "Trace";
            },
            services =>
            {
                services.AddSingleton<IExchangeRecordSink>(sink);

                if (logs is not null)
                {
                    services.AddSingleton<ILoggerProvider>(logs);
                }
            });

    private static async Task<HttpResponseMessage> SendAsync(GatewayFixture fixture, bool streaming)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(
                streaming
                    ? """{"model":"m","stream":true,"messages":[{"role":"user","content":"hi"}]}"""
                    : """{"model":"m","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"),
        };

        return await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
}
