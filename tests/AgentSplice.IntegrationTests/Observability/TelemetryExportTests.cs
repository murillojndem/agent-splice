using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text;
using AgentSplice.Application.Diagnostics;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.Observability;
using AgentSplice.TestSupport.FakeUpstream;
using AgentSplice.TestSupport.Logging;
using AgentSplice.TestSupport.Otlp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using Xunit;

namespace AgentSplice.IntegrationTests.Observability;

/// <summary>
/// OTLP export against a real receiver (FR-OBS-001, FR-OBS-002, FR-OBS-006, FR-OBS-007, ADR 0014).
/// </summary>
/// <remarks>
/// Three claims, and the second and third matter more than the first. Spans and metrics reach a
/// collector over either transport; nothing a client sent or a model produced is in what reaches
/// it; and nothing reaches it at all unless the operator turned export on — not even when the
/// standard OpenTelemetry endpoint variable says where a collector is.
///
/// Waiting is unavoidable. The exchange span ends after the client response has completed, and the
/// SDK exports from its own threads, so each wait flushes the providers and polls until the payload
/// it needs has arrived or a deadline passes.
/// </remarks>
public sealed class TelemetryExportTests
{
    private const string PromptSentinel = "SENTINEL-PROMPT-export-7d21";
    private const string CompletionSentinel = "SENTINEL-COMPLETION-export-5c90";
    private const string ToolArgumentSentinel = "SENTINEL-TOOLARG-export-e3a4";
    private const string ModelSentinel = "SENTINEL-MODEL-export-81fb";
    private const string ClientRequestIdSentinel = "SENTINEL-REQUESTID-export-0a6d";
    private const string ApiKeySentinel = "SENTINEL-APIKEY-sk-export-4be2";
    private const string ApiKeyVariable = "AGENTSPLICE_TEST_EXPORT_KEY";

    private static readonly TimeSpan ExportDeadline = TimeSpan.FromSeconds(15);

    private static readonly string Completion =
        """
        {"id":"chatcmpl-1","object":"chat.completion","model":"MODEL",
         "choices":[{"index":0,"message":{"role":"assistant","content":"COMPLETION",
           "tool_calls":[{"id":"c1","type":"function",
             "function":{"name":"read_file","arguments":"{\"path\":\"TOOLARG\"}"}}]},
           "finish_reason":"tool_calls"}],
         "usage":{"prompt_tokens":11,"completion_tokens":3,"total_tokens":14}}
        """
        .Replace("MODEL", ModelSentinel, StringComparison.Ordinal)
        .Replace("COMPLETION", CompletionSentinel, StringComparison.Ordinal)
        .Replace("TOOLARG", ToolArgumentSentinel, StringComparison.Ordinal);

    [Fact]
    public async Task Spans_reach_the_collector_over_http_protobuf_under_the_trace_the_client_was_given()
    {
        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        await using var fixture = await StartAsync(collector, "HttpProtobuf");

        using var response = await CompleteAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var traceId = Convert.FromHexString(response.Headers.GetValues("x-agentsplice-trace-id").Single());
        var spans = await WaitForExportAsync(fixture, collector, FakeOtlpCollector.HttpTracesPath, "agentsplice.exchange");

        Assert.Equal("HTTP/1.1", spans.Protocol);
        Assert.Equal("application/x-protobuf", spans.ContentType);

        // The span the collector holds is the one x-agentsplice-trace-id named. Without this an
        // operator could be handed an identifier that finds nothing in the backend it was exported to.
        Assert.True(spans.Contains(traceId), "The exported spans do not carry the trace identifier the client was given.");

        Assert.True(spans.Contains("agentsplice.provider.request"));
        Assert.True(spans.Contains("agentsplice.runtime.id"));
        Assert.True(spans.Contains(GatewayFixture.RuntimeId));
        Assert.True(spans.Contains("service.name"));
        Assert.True(spans.Contains("agentsplice"));
    }

    [Fact]
    public async Task Metrics_reach_the_collector_over_http_protobuf()
    {
        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        await using var fixture = await StartAsync(collector, "HttpProtobuf");

        using var response = await CompleteAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var metrics = await WaitForExportAsync(fixture, collector, FakeOtlpCollector.HttpMetricsPath, "agentsplice.exchanges");

        Assert.Equal("application/x-protobuf", metrics.ContentType);
        Assert.True(metrics.Contains("agentsplice.exchange.duration"));
        Assert.True(metrics.Contains("agentsplice.exchange.status"));
        Assert.True(metrics.Contains("agentsplice.prompt.tokens"));
    }

    [Fact]
    public async Task A_runtime_error_inside_a_200_is_exported_as_the_runtimes_error()
    {
        // The status class said 2xx, the exchange said completed, and the span was not marked: a
        // backend counted a runtime that reported its failure as a success (ADR 0026).
        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        await using var fixture = await StartAsync(collector, "HttpProtobuf");

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.Json("""{"error":{"message":"The model crashed","type":"server_error"}}"""));

        var stopped = new ConcurrentQueue<Activity>();

        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == TelemetryNames.ActivitySources.Exchange,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };

        ActivitySource.AddActivityListener(listener);

        using var response = await CompleteAsync(fixture, scripted: true);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var traceId = response.Headers.GetValues("x-agentsplice-trace-id").Single();

        var metrics = await WaitForExportAsync(fixture, collector, FakeOtlpCollector.HttpMetricsPath, TelemetryNames.Attributes.UpstreamOutcome);

        Assert.True(metrics.Contains("ErrorReported"));

        var span = await WaitForSpanAsync(stopped, traceId);

        Assert.Equal(ActivityStatusCode.Error, span.Status);
        Assert.Equal("ErrorReported", span.GetTagItem(TelemetryNames.Attributes.UpstreamOutcome));

        // AgentSplice did not fail, so the error type stays absent: it names AgentSplice's own failures.
        Assert.Null(span.GetTagItem(TelemetryNames.Attributes.ErrorType));
    }

    [Fact]
    public async Task A_clients_tracestate_never_reaches_the_collector()
    {
        // An activity with no tracestate of its own reads its parent's, and the exchange span's
        // parent is ASP.NET Core's request activity, which took the client's header. Exported, a
        // value the client chose rode along on every span of the exchange (ADR 0027).
        const string Sentinel = "SENTINEL-TRACESTATE-77d1";

        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        await using var fixture = await StartAsync(collector, "HttpProtobuf");

        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent("""{"model":"m","messages":[{"role":"user","content":"hi"}]}""", Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("traceparent", "00-0af7651916cd43dd8448eb211c80319c-b7ad6b7169203331-01");
        request.Headers.TryAddWithoutValidation("tracestate", "vendor=" + Sentinel);

        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var spans = await WaitForExportAsync(fixture, collector, FakeOtlpCollector.HttpTracesPath, "agentsplice.provider.request");

        // The trace the client began is kept; only its state is not.
        Assert.True(spans.Contains(Convert.FromHexString("0af7651916cd43dd8448eb211c80319c")));
        Assert.False(spans.Contains(Sentinel), "The client's tracestate was exported.");
    }

    [Fact]
    public async Task Spans_and_metrics_reach_the_collector_over_grpc()
    {
        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.Grpc);
        await using var fixture = await StartAsync(collector, "Grpc");

        using var response = await CompleteAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var spans = await WaitForExportAsync(fixture, collector, FakeOtlpCollector.GrpcTracesPath, "agentsplice.exchange");
        var metrics = await WaitForExportAsync(fixture, collector, FakeOtlpCollector.GrpcMetricsPath, "agentsplice.exchanges");

        Assert.All(new[] { spans, metrics }, export =>
        {
            Assert.Equal("HTTP/2", export.Protocol);
            Assert.Equal("application/grpc", export.ContentType);
        });
    }

    [Theory]
    [InlineData(PromptSentinel)]
    [InlineData(CompletionSentinel)]
    [InlineData(ToolArgumentSentinel)]
    [InlineData(ModelSentinel)]
    [InlineData(ClientRequestIdSentinel)]
    [InlineData(ApiKeySentinel)]
    public async Task No_sensitive_value_reaches_the_collector(string sentinel)
    {
        // A buffered and a streamed exchange through the same host, because they record through
        // different paths and the streamed one decodes every event it forwards. The model sentinel is
        // routed by pass-through so that it is the identifier the gateway actually forwarded, not one
        // it rejected; the request-id sentinel is the client's own correlation token, which is
        // client-chosen text and may carry anything (FR-OBS-006).
        Environment.SetEnvironmentVariable(ApiKeyVariable, ApiKeySentinel);

        try
        {
            await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
            await using var fixture = await StartAsync(
                collector,
                "HttpProtobuf",
                settings => settings[GatewayFixture.RuntimeKey(0, "apiKeyEnvironmentVariable")] = ApiKeyVariable);

            using (var buffered = await CompleteAsync(fixture))
            {
                Assert.Equal(HttpStatusCode.OK, buffered.StatusCode);
            }

            fixture.Upstream.EnqueueFor(
                "/v1/chat/completions",
                SseScript.Create()
                    .Data("""{"model":"MODEL","choices":[{"delta":{"content":"COMPLETION"}}]}"""
                        .Replace("MODEL", ModelSentinel, StringComparison.Ordinal)
                        .Replace("COMPLETION", CompletionSentinel, StringComparison.Ordinal))
                    .Data("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read_file","arguments":"TOOLARG"}}]}}]}"""
                        .Replace("TOOLARG", ToolArgumentSentinel, StringComparison.Ordinal))
                    .Done()
                    .Build());

            using (var streamed = await CompleteAsync(fixture, streaming: true, scripted: true))
            {
                Assert.Equal(HttpStatusCode.OK, streamed.StatusCode);
                Assert.Contains(CompletionSentinel, await streamed.Content.ReadAsStringAsync(), StringComparison.Ordinal);
            }

            // Both exchanges must have been exported before absence means anything: the stream span
            // exists only for the streamed one, and the stream termination dimension only on its
            // metrics.
            await WaitForExportAsync(fixture, collector, FakeOtlpCollector.HttpTracesPath, "agentsplice.stream");
            await WaitForExportAsync(fixture, collector, FakeOtlpCollector.HttpMetricsPath, "agentsplice.stream.termination");

            var offending = collector.Received
                .Where(export => export.Contains(sentinel))
                .Select(export => export.Path)
                .ToArray();

            Assert.True(
                offending.Length == 0,
                $"'{sentinel}' reached the collector in: {string.Join(", ", offending)}");
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApiKeyVariable, null);
        }
    }

    [Fact]
    public async Task Nothing_is_exported_while_export_is_off_even_with_an_endpoint_and_the_standard_variable_set()
    {
        // The standard variable names the collector outright, and the AgentSplice endpoint does too.
        // Neither is consent: only agentsplice:telemetry:otlp:enabled is.
        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        var logs = new CapturingLoggerProvider();

        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", collector.BaseAddress.AbsoluteUri);
        Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", "http/protobuf");

        try
        {
            await using var fixture = await StartAsync(
                collector,
                "HttpProtobuf",
                settings => settings["agentsplice:telemetry:otlp:enabled"] = "false",
                logs);

            using var response = await CompleteAsync(fixture);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            // Flushed repeatedly across a window the enabled tests export well inside, so a span that
            // ended after the first flush still had every chance to be sent.
            var window = Stopwatch.StartNew();

            while (window.Elapsed < TimeSpan.FromSeconds(1))
            {
                Flush(fixture);
                await Task.Delay(50);
            }

            Assert.Empty(collector.Received);

            // The deterministic half: the tracer provider is built before the first exchange, and it
            // announces an exporter whenever it adds one.
            Assert.DoesNotContain(logs.Records, record => record.EventId == GatewayEventIds.TelemetryExportEnabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT", null);
            Environment.SetEnvironmentVariable("OTEL_EXPORTER_OTLP_PROTOCOL", null);
        }
    }

    [Fact]
    public async Task Turning_export_on_is_announced_with_its_transport_and_destination()
    {
        // Export sends a description of every exchange off the process, so the log says so once, at
        // startup, where an operator reviewing what a deployment does will find it.
        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        var logs = new CapturingLoggerProvider();

        await using var fixture = await StartAsync(collector, "HttpProtobuf", logs: logs);

        using var response = await CompleteAsync(fixture);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var announcement = Assert.Single(
            logs.Records,
            record => record.EventId == GatewayEventIds.TelemetryExportEnabled);

        Assert.Equal(LogLevel.Information, announcement.Level);
        Assert.Contains(announcement.AllText(), text => text.Contains(collector.BaseAddress.AbsoluteUri, StringComparison.Ordinal));
        Assert.Contains(announcement.AllText(), text => text.Contains("HttpProtobuf", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_collector_that_cannot_be_reached_does_not_affect_the_exchange()
    {
        // Export must never be able to fail an exchange (FR-DATA-009 applies the same rule to
        // persistence). The receiver is started and stopped so the endpoint is a real loopback port
        // with nothing listening, which fails fast and deterministically.
        var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        await collector.DisposeAsync();

        await using var fixture = await StartAsync(collector, "HttpProtobuf");

        using var response = await CompleteAsync(fixture);
        var body = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(CompletionSentinel, body, StringComparison.Ordinal);

        // Flushing reports the failure to its caller and throws nothing.
        Flush(fixture);
    }

    [Fact]
    public async Task Logs_are_not_routed_through_opentelemetry()
    {
        // Traces and metrics only. The log pipeline is the one FR-OBS-007 constrains, and exporting
        // it is a decision ADR 0014 did not take; an OpenTelemetry logger provider arriving as a side
        // effect of a package upgrade would take it silently.
        await using var collector = await FakeOtlpCollector.StartAsync(OtlpReceiverTransport.HttpProtobuf);
        await using var fixture = await StartAsync(collector, "HttpProtobuf");

        var providers = fixture.Services.GetServices<ILoggerProvider>();

        Assert.DoesNotContain(
            providers,
            provider => provider.GetType().FullName?.StartsWith("OpenTelemetry", StringComparison.Ordinal) == true);
    }

    private static Task<GatewayFixture> StartAsync(
        FakeOtlpCollector collector,
        string protocol,
        Action<Dictionary<string, string?>>? configure = null,
        CapturingLoggerProvider? logs = null) =>
        GatewayFixture.StartAsync(
            settings =>
            {
                settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings["agentsplice:telemetry:otlp:enabled"] = "true";
                settings["agentsplice:telemetry:otlp:endpoint"] = collector.BaseAddress.AbsoluteUri;
                settings["agentsplice:telemetry:otlp:protocol"] = protocol;
                configure?.Invoke(settings);
            },
            logs is null ? null : services => services.AddSingleton<ILoggerProvider>(logs));

    /// <summary>Sends one completion carrying every sentinel a client can put in a request.</summary>
    /// <param name="fixture">The gateway.</param>
    /// <param name="streaming">Whether to ask for a stream.</param>
    /// <param name="scripted">
    /// Whether the caller already queued the upstream answer. Otherwise the buffered completion
    /// fixture is queued here.
    /// </param>
    private static async Task<HttpResponseMessage> CompleteAsync(
        GatewayFixture fixture,
        bool streaming = false,
        bool scripted = false)
    {
        if (!scripted)
        {
            fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));
        }

        var body = (streaming
                ? """{"model":"MODEL","stream":true,"messages":[{"role":"user","content":"PROMPT"}]}"""
                : """{"model":"MODEL","messages":[{"role":"user","content":"PROMPT"}]}""")
            .Replace("MODEL", ModelSentinel, StringComparison.Ordinal)
            .Replace("PROMPT", PromptSentinel, StringComparison.Ordinal);

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

        request.Headers.TryAddWithoutValidation("x-request-id", ClientRequestIdSentinel);

        return await fixture.Client.SendAsync(request);
    }

    private static async Task<ReceivedOtlpExport> WaitForExportAsync(
        GatewayFixture fixture,
        FakeOtlpCollector collector,
        string path,
        string carrying)
    {
        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            Flush(fixture);

            var match = collector.Received.FirstOrDefault(export =>
                string.Equals(export.Path, path, StringComparison.Ordinal) && export.Contains(carrying));

            if (match is not null)
            {
                return match;
            }

            if (elapsed.Elapsed > ExportDeadline)
            {
                throw new TimeoutException(
                    $"No export to {path} carrying '{carrying}' arrived within {ExportDeadline}. Received: {string.Join(", ", collector.Received.Select(export => export.Path))}");
            }

            await Task.Delay(50);
        }
    }

    /// <summary>Waits for the exchange span of one trace to stop, which follows the response's write.</summary>
    private static async Task<Activity> WaitForSpanAsync(ConcurrentQueue<Activity> stopped, string traceId)
    {
        var elapsed = Stopwatch.StartNew();

        while (true)
        {
            if (stopped.FirstOrDefault(activity => activity.TraceId.ToHexString() == traceId) is { } span)
            {
                return span;
            }

            if (elapsed.Elapsed > ExportDeadline)
            {
                throw new TimeoutException($"The exchange span of trace {traceId} did not stop within {ExportDeadline}.");
            }

            await Task.Delay(20);
        }
    }

    private static void Flush(GatewayFixture fixture)
    {
        fixture.Services.GetRequiredService<TracerProvider>().ForceFlush();
        fixture.Services.GetRequiredService<MeterProvider>().ForceFlush();
    }
}
