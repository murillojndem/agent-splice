using System.Net;
using System.Text;
using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Runtimes;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using AgentSplice.TestSupport.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Privacy;

/// <summary>
/// No prompt, no model output, no tool argument, and no credential reaches a log
/// (docs/SPECIFICATION.md FR-OBS-007, docs/SECURITY.md, docs/TESTING.md "Trace and privacy").
/// </summary>
/// <remarks>
/// Logging is set to <see cref="LogLevel.Trace"/> on purpose. The claim
/// worth making is that content is absent even at the most verbose setting, not that the default
/// level happens to filter it out — the weak form of this test would pass on a gateway that logs
/// prompts at Debug.
///
/// Each sensitive value is a distinct sentinel so a failure names which one escaped.
/// </remarks>
public sealed class LogRedactionTests : IDisposable
{
    private const string PromptSentinel = "SENTINEL-PROMPT-9f3c1a";
    private const string CompletionSentinel = "SENTINEL-COMPLETION-2b7e44";
    private const string ToolArgumentSentinel = "SENTINEL-TOOLARG-c81d05";
    private const string ApiKeySentinel = "SENTINEL-APIKEY-sk-6a2f9b";
    private const string ApiKeyVariable = "AGENTSPLICE_TEST_REDACTION_KEY";

    private static readonly string Completion =
        """
        {"id":"chatcmpl-1","object":"chat.completion","model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"COMPLETION",
           "tool_calls":[{"id":"c1","type":"function",
             "function":{"name":"read_file","arguments":"{\"path\":\"TOOLARG\"}"}}]},
           "finish_reason":"tool_calls"}],
         "usage":{"prompt_tokens":11,"completion_tokens":3,"total_tokens":14}}
        """
        .Replace("COMPLETION", CompletionSentinel, StringComparison.Ordinal)
        .Replace("TOOLARG", ToolArgumentSentinel, StringComparison.Ordinal);

    public LogRedactionTests() =>
        Environment.SetEnvironmentVariable(ApiKeyVariable, ApiKeySentinel);

    public void Dispose() => Environment.SetEnvironmentVariable(ApiKeyVariable, null);

    [Theory]
    [InlineData(PromptSentinel)]
    [InlineData(CompletionSentinel)]
    [InlineData(ToolArgumentSentinel)]
    [InlineData(ApiKeySentinel)]
    public async Task No_sensitive_value_appears_in_any_log_channel_at_any_level(string sentinel)
    {
        var logs = await ProxyAsync();

        var offending = logs.AllText
            .Where(text => text.Contains(sentinel, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"'{sentinel}' reached a log: {string.Join(" | ", offending)}");
    }

    [Fact]
    public async Task Logging_really_was_capturing_at_trace_level()
    {
        // Guards the tests above: if nothing were captured they would pass vacuously.
        var logs = await ProxyAsync();

        Assert.NotEmpty(logs.Records);
    }

    [Fact]
    public async Task A_failing_exchange_leaks_nothing_either()
    {
        // The error path builds messages and logs an exception, which is where content is most
        // likely to escape.
        var logs = await ProxyAsync(
            script: UpstreamResponseScripts.Json(
                """{"error":{"message":"LEAK"}}""".Replace("LEAK", CompletionSentinel, StringComparison.Ordinal),
                500));

        // The runtime's error body carries the completion sentinel, so the body is checked too: it
        // was planted and never asserted, and a regression that logged a relayed error body passed.
        Assert.DoesNotContain(
            logs.AllText,
            text => text.Contains(PromptSentinel, StringComparison.Ordinal)
                || text.Contains(CompletionSentinel, StringComparison.Ordinal)
                || text.Contains(ApiKeySentinel, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_model_list_fault_never_logs_the_clients_correlation_token()
    {
        // The August sweep took the client's x-request-id out of four log sites and missed this one,
        // on GET /v1/models, which the completion-only test above cannot reach.
        const string Sentinel = "SENTINEL-REQUESTID-model-list";

        var logs = new CapturingLoggerProvider();

        await using var fixture = await GatewayFixture.StartAsync(
            settings => settings["Logging:LogLevel:Default"] = "Trace",
            services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);

                // A provider whose discovery throws: the one path into the model list's fault log
                // once the catalogue reader classifies every malformed answer itself.
                services.RemoveAll<IModelRuntimeProvider>();
                services.AddSingleton<IModelRuntimeProvider, FaultingProvider>();
            });

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/models", UriKind.Relative));
        request.Headers.TryAddWithoutValidation("x-request-id", Sentinel);

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains(logs.Records, record => record.EventId.Id == GatewayEventIds.ModelListFailed.Id);
        Assert.DoesNotContain(logs.AllText, text => text.Contains(Sentinel, StringComparison.Ordinal));

        // The client still has it, on the response it was sent.
        Assert.Equal(Sentinel, response.Headers.GetValues("x-agentsplice-request-id").Single());
    }

    [Fact]
    public async Task A_faulted_exchange_is_logged_by_its_exchange_id_and_nothing_it_carried()
    {
        // The path that logs an exception: a provider that throws mid-request, as a defect would. The
        // log is how an operator finds the exchange, so it must name the identifier the client was
        // given, and an exception's text is where content most often escapes, so every sentinel is
        // searched for in it too.
        var (logs, response) = await FaultAsync(services =>
        {
            services.RemoveAll<IModelRuntimeProvider>();
            services.AddSingleton<IModelRuntimeProvider, ThrowingCompletionProvider>();
        });

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);

        AssertLoggedByExchangeId(logs, GatewayEventIds.ExchangeFaulted, response);
    }

    [Fact]
    public async Task Evidence_that_could_not_be_recorded_is_logged_by_its_exchange_id_and_nothing_it_carried()
    {
        var (logs, response) = await FaultAsync(services =>
            services.AddSingleton<IExchangeRecordSink, ThrowingRecordSink>());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        AssertLoggedByExchangeId(logs, GatewayEventIds.EvidenceRecordingFailed, response);
    }

    private static void AssertLoggedByExchangeId(CapturingLoggerProvider logs, EventId eventId, HttpResponseMessage response)
    {
        var exchangeId = response.Headers.GetValues("x-agentsplice-exchange-id").Single();
        var record = Assert.Single(logs.Records, candidate => candidate.EventId.Id == eventId.Id);

        Assert.NotNull(record.Exception);
        Assert.Contains("ExchangeId=" + exchangeId, record.StateValues);

        foreach (var sentinel in new[] { PromptSentinel, CompletionSentinel, ToolArgumentSentinel, ApiKeySentinel })
        {
            Assert.DoesNotContain(logs.AllText, text => text.Contains(sentinel, StringComparison.Ordinal));
        }
    }

    private static async Task<(CapturingLoggerProvider Logs, HttpResponseMessage Response)> FaultAsync(
        Action<IServiceCollection> fault)
    {
        var logs = new CapturingLoggerProvider();

        await using var fixture = await GatewayFixture.StartAsync(
            settings =>
            {
                settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings[GatewayFixture.RuntimeKey(0, "apiKeyEnvironmentVariable")] = ApiKeyVariable;
                settings["Logging:LogLevel:Default"] = "Trace";
            },
            services =>
            {
                services.AddSingleton<ILoggerProvider>(logs);
                fault(services);
            });

        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var content = new StringContent(
            """{"model":"m","messages":[{"role":"user","content":"PROMPT"}]}""".Replace("PROMPT", PromptSentinel, StringComparison.Ordinal),
            Encoding.UTF8,
            "application/json");

        var response = await fixture.Client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content);

        // The evidence is handed over after the response; the log line that says it failed follows.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);

        while (!logs.Records.Any(record => record.EventId.Id is 1001 or 1002) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(20));
        }

        return (logs, response);
    }

    /// <summary>A provider whose completion throws, as a defect inside it would.</summary>
    private sealed class ThrowingCompletionProvider : IModelRuntimeProvider
    {
        public string ProviderKey => "lmstudio";

        public Task<RuntimeModelListResult> ListModelsAsync(RuntimeTarget target, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderCompletionResult> CompleteAsync(ProviderCompletionRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A fault inside the provider, raised for the test.");

        public Task<ProviderStreamResult> StreamAsync(ProviderCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>A sink that refuses every record, as a broken store behind it would.</summary>
    private sealed class ThrowingRecordSink : IExchangeRecordSink
    {
        public ValueTask RecordAsync(ExchangeRecord record, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A fault inside the sink, raised for the test.");
    }

    /// <summary>A runtime provider whose model discovery throws, as a defect would.</summary>
    private sealed class FaultingProvider : IModelRuntimeProvider
    {
        public string ProviderKey => "lmstudio";

        public Task<RuntimeModelListResult> ListModelsAsync(RuntimeTarget target, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("A fault inside discovery, raised for the test.");

        public Task<ProviderCompletionResult> CompleteAsync(ProviderCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<ProviderStreamResult> StreamAsync(ProviderCompletionRequest request, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    [Fact]
    public async Task No_log_message_template_is_built_by_string_interpolation()
    {
        // A structured logger records the template and its arguments separately. If a message were
        // interpolated, the template would already contain the substituted value and structured
        // state would be empty, which is how content silently escapes redaction.
        var logs = await ProxyAsync();

        Assert.All(
            logs.Records.Where(record => record.Category.StartsWith("AgentSplice", StringComparison.Ordinal)),
            record => Assert.DoesNotContain("{", record.Message, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(PromptSentinel)]
    [InlineData(CompletionSentinel)]
    [InlineData(ToolArgumentSentinel)]
    [InlineData(ApiKeySentinel)]
    public async Task No_sensitive_value_escapes_while_a_response_is_being_streamed(string sentinel)
    {
        // The streaming path decodes every event to gather evidence, which is exactly the kind of
        // work that ends up logging what it just parsed. It also handles many small payloads rather
        // than one body, so a leak here would repeat per token.
        var logs = await ProxyAsync(
            script: SseScript.Create()
                .Data("""{"choices":[{"delta":{"content":"COMPLETION"}}]}"""
                    .Replace("COMPLETION", CompletionSentinel, StringComparison.Ordinal))
                .Data("""{"choices":[{"delta":{"tool_calls":[{"index":0,"id":"c1","function":{"name":"read_file","arguments":"TOOLARG"}}]}}]}"""
                    .Replace("TOOLARG", ToolArgumentSentinel, StringComparison.Ordinal))
                .Done()
                .Build(),
            streaming: true);

        var offending = logs.AllText
            .Where(text => text.Contains(sentinel, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"'{sentinel}' reached a log while streaming: {string.Join(" | ", offending)}");
    }

    [Fact]
    public async Task A_correlation_token_the_client_chose_never_reaches_a_log()
    {
        // x-request-id is accepted as up to 128 characters of printable ASCII and adopted as the
        // exchange's PublicRequestId. Printable is not the same as safe: it stops header injection
        // and does nothing about a client putting a patient name, a ticket subject, or a file path
        // in it. Anything that logs the correlation token writes client-chosen text into the
        // operator's logs, so nothing does — the logs carry ExchangeId, which AgentSplice generates
        // and returns as x-agentsplice-exchange-id.
        const string Sentinel = "SENTINEL-REQUESTID-patient-name";

        var logs = await ProxyAsync(clientRequestId: Sentinel);

        var offending = logs.AllText
            .Where(text => text.Contains(Sentinel, StringComparison.Ordinal))
            .ToArray();

        Assert.True(
            offending.Length == 0,
            $"The client's own correlation token reached a log: {string.Join(" | ", offending)}");
    }

    private static async Task<CapturingLoggerProvider> ProxyAsync(
        UpstreamResponseScript? script = null,
        bool streaming = false,
        string? clientRequestId = null)
    {
        var logs = new CapturingLoggerProvider();

        await using var fixture = await GatewayFixture.StartAsync(
            settings =>
            {
                settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
                settings[GatewayFixture.RuntimeKey(0, "apiKeyEnvironmentVariable")] = ApiKeyVariable;
                settings["Logging:LogLevel:Default"] = "Trace";
            },
            services => services.AddSingleton<ILoggerProvider>(logs));

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            script ?? UpstreamResponseScripts.Json(Completion));

        var body = streaming
            ? """{"model":"m","stream":true,"messages":[{"role":"user","content":"PROMPT"}]}"""
            : """{"model":"m","messages":[{"role":"user","content":"PROMPT"}]}""";

        using var content = new StringContent(
            body.Replace("PROMPT", PromptSentinel, StringComparison.Ordinal),
            Encoding.UTF8,
            "application/json");

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = content,
        };

        if (clientRequestId is not null)
        {
            request.Headers.TryAddWithoutValidation("x-request-id", clientRequestId);
        }

        using var response = await fixture.Client.SendAsync(request);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);

        return logs;
    }
}
