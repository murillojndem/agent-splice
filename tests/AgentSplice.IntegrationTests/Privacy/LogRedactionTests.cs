using System.Net;
using System.Text;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using AgentSplice.TestSupport.Logging;
using Microsoft.Extensions.DependencyInjection;
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

        Assert.DoesNotContain(
            logs.AllText,
            text => text.Contains(PromptSentinel, StringComparison.Ordinal)
                || text.Contains(ApiKeySentinel, StringComparison.Ordinal));
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

    private static async Task<CapturingLoggerProvider> ProxyAsync(
        UpstreamResponseScript? script = null,
        bool streaming = false)
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

        using var response = await fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            content);

        Assert.NotEqual(HttpStatusCode.NotFound, response.StatusCode);

        return logs;
    }
}
