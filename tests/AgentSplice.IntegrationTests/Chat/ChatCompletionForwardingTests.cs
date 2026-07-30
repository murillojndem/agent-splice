using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// Exact forwarding of a non-streaming completion, end to end against a real listener
/// (docs/ROADMAP.md Stage 1A: "forwarded fields are verified against the fake upstream").
/// </summary>
/// <remarks>
/// Every forwarding assertion compares the bytes the upstream actually received. A text comparison
/// would pass on a body that had been reparsed and re-emitted, which is exactly the transformation
/// the design forbids.
/// </remarks>
public sealed class ChatCompletionForwardingTests
{
    private const string Completion = """
        {"id":"chatcmpl-1","object":"chat.completion","created":1700000000,"model":"qwen3.6-27b-mtp",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":41,"completion_tokens":7,"total_tokens":48}}
        """;

    [Fact]
    public async Task A_completion_is_forwarded_and_its_answer_relayed()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture, Minimal("qwen3.6-27b-mtp"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Completion, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task The_upstream_body_is_byte_identical_when_no_alias_renames_the_model()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        var sent = Minimal("qwen3.6-27b-mtp");
        await PostAsync(fixture, sent);

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.Equal(Encoding.UTF8.GetBytes(sent), recorded.Body.ToArray());
    }

    [Fact]
    public async Task Unknown_fields_whitespace_and_number_formatting_reach_the_upstream_verbatim()
    {
        // The properties a reparse-and-re-emit implementation would silently destroy.
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        const string Sent = """
            {
              "model" : "qwen3.6-27b-mtp",
              "temperature" : 1.0,
              "seed" : 42,
              "reasoning_effort" : "high",
              "messages" : [ { "role": "user", "content": "ABC" } ]
            }
            """;

        await PostAsync(fixture, Sent);

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.Equal(Encoding.UTF8.GetBytes(Sent), recorded.Body.ToArray());
    }

    [Fact]
    public async Task Tools_and_tool_choice_reach_the_upstream_verbatim()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        const string Sent = """
            {"model":"qwen3.6-27b-mtp","messages":[{"role":"user","content":"hi"}],
             "tools":[{"type":"function","function":{"name":"read_file","parameters":{"type":"object"}}}],
             "tool_choice":{"type":"function","function":{"name":"read_file"}}}
            """;

        await PostAsync(fixture, Sent);

        Assert.Equal(
            Encoding.UTF8.GetBytes(Sent),
            Assert.Single(fixture.Upstream.ReceivedRequests).Body.ToArray());
    }

    [Fact]
    public async Task Only_the_model_value_differs_when_an_alias_renames_the_model()
    {
        await using var fixture = await StartAsync(WithAlias);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        const string Sent =
            """{"model":"local-coder","temperature":1.0,"messages":[{"role":"user","content":"ABC"}]}""";
        const string Expected =
            """{"model":"qwen3.6-27b-mtp","temperature":1.0,"messages":[{"role":"user","content":"ABC"}]}""";

        await PostAsync(fixture, Sent);

        Assert.Equal(
            Encoding.UTF8.GetBytes(Expected),
            Assert.Single(fixture.Upstream.ReceivedRequests).Body.ToArray());
    }

    [Fact]
    public async Task An_alias_that_does_not_rename_forwards_the_original_bytes()
    {
        await using var fixture = await StartAsync(settings =>
        {
            settings[GatewayFixture.AliasKey(0, "id")] = "qwen3.6-27b-mtp";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
        });

        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        var sent = Minimal("qwen3.6-27b-mtp");
        await PostAsync(fixture, sent);

        Assert.Equal(
            Encoding.UTF8.GetBytes(sent),
            Assert.Single(fixture.Upstream.ReceivedRequests).Body.ToArray());
    }

    [Fact]
    public async Task The_request_reaches_the_chat_completions_path_beneath_the_configured_prefix()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        await PostAsync(fixture, Minimal("qwen3.6-27b-mtp"));

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.Equal("POST", recorded.Method);
        Assert.Equal("/v1/chat/completions", recorded.Path);
    }

    [Fact]
    public async Task The_client_authorization_header_is_never_forwarded()
    {
        // The client's credential is not the gateway's to present to a runtime.
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var request = Post(Minimal("qwen3.6-27b-mtp"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-secret-value");

        using var response = await fixture.Client.SendAsync(request);

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.False(recorded.Headers.ContainsKey("Authorization"));
        Assert.DoesNotContain("client-secret-value", recorded.BodyAsText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_gateway_request_id_is_forwarded_upstream_for_correlation()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var request = Post(Minimal("qwen3.6-27b-mtp"));
        request.Headers.Add("x-request-id", "trace-me-1");

        await fixture.Client.SendAsync(request);

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.Equal("trace-me-1", Assert.Single(recorded.Headers["x-request-id"]));
    }

    [Fact]
    public async Task The_response_model_field_is_not_rewritten_back_to_the_alias()
    {
        // Rewriting it is not required for routing, so P-002 forbids it.
        await using var fixture = await StartAsync(WithAlias);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture, Minimal("local-coder"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal("qwen3.6-27b-mtp", document.RootElement.GetProperty("model").GetString());
    }

    [Fact]
    public async Task A_successful_completion_carries_the_correlation_headers()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture, Minimal("qwen3.6-27b-mtp"));

        Assert.True(response.Headers.Contains("x-agentsplice-request-id"));
        Assert.True(Guid.TryParse(
            response.Headers.GetValues("x-agentsplice-exchange-id").Single(),
            out _));
        Assert.Equal(
            GatewayFixture.RuntimeId,
            response.Headers.GetValues("x-agentsplice-runtime").Single());
    }

    [Fact]
    public async Task An_unresolvable_model_is_reported_before_any_upstream_request_is_opened()
    {
        // No pass-through here: with a default runtime configured nothing is unresolvable, which is
        // the whole point of that setting.
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(
            """{"object":"list","data":[{"id":"qwen3.6-27b-mtp","object":"model"}]}"""));

        using var response = await PostAsync(fixture, Minimal("never-heard-of-it"));
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(
            "agentsplice_model_not_found",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.DoesNotContain(
            fixture.Upstream.ReceivedRequests,
            recorded => recorded.Path == "/v1/chat/completions");
    }

    [Fact]
    public async Task An_error_before_routing_carries_no_runtime_header()
    {
        // Naming a runtime that was never involved would be a fabrication.
        await using var fixture = await StartAsync();

        using var response = await PostAsync(fixture, "not json at all");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("x-agentsplice-request-id"));
        Assert.False(response.Headers.Contains("x-agentsplice-runtime"));
    }

    [Fact]
    public async Task A_streaming_request_is_refused_with_a_stable_message()
    {
        await using var fixture = await StartAsync();

        using var response = await PostAsync(
            fixture,
            """{"model":"qwen3.6-27b-mtp","stream":true,"messages":[{"role":"user","content":"hi"}]}""");

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var error = document.RootElement.GetProperty("error");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("stream", error.GetProperty("param").GetString());
        Assert.Equal("Streaming is not supported by this build.", error.GetProperty("message").GetString());
        Assert.Empty(fixture.Upstream.ReceivedRequests);
    }

    private static void WithAlias(Dictionary<string, string?> settings)
    {
        settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
        settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
        settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
    }

    private static Task<GatewayFixture> StartAsync(Action<Dictionary<string, string?>>? configure = null) =>
        GatewayFixture.StartAsync(settings =>
        {
            // Pass-through keeps these tests about forwarding rather than about discovery.
            settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            configure?.Invoke(settings);
        });

    private static string Minimal(string model) =>
        $$"""{"model":"{{model}}","messages":[{"role":"user","content":"hi"}]}""";

    private static HttpRequestMessage Post(string body) =>
        new(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        };

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture fixture, string body) =>
        fixture.Client.SendAsync(Post(body));
}
