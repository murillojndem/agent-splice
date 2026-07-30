using System.Net;
using System.Text;
using System.Text.Json;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// Error translation and upstream passthrough, one case per documented row
/// (docs/API.md "Error status mapping", ADR 0008).
/// </summary>
public sealed class ChatCompletionErrorTests
{
    /// <summary>
    /// The two honest classifications of a connection reset partway through a response body.
    /// </summary>
    /// <remarks>
    /// Which one occurs depends on whether the reset outran the bytes that preceded it. If it did,
    /// the gateway genuinely never saw a response, and "the runtime could not be reached" is the
    /// truthful account of that.
    /// </remarks>
    private static readonly string[] ResetMidBodyCodes =
    [
        "agentsplice_invalid_upstream_response",
        "agentsplice_runtime_unavailable",
    ];

    [Theory]
    [InlineData("not json at all")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"messages":[{"role":"user"}]}""")]
    [InlineData("""{"model":"m"}""")]
    [InlineData("""{"model":"m","messages":[]}""")]
    public async Task An_invalid_request_is_rejected_with_the_stable_code(string body)
    {
        await using var fixture = await StartAsync();

        var error = await PostForErrorAsync(fixture, body, HttpStatusCode.BadRequest);

        Assert.Equal("agentsplice_invalid_request", error.GetProperty("code").GetString());
        Assert.Equal("invalid_request_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_invalid_request_never_reaches_the_runtime()
    {
        await using var fixture = await StartAsync();

        await PostAsync(fixture, "not json at all");

        Assert.Empty(fixture.Upstream.ReceivedRequests);
    }

    [Fact]
    public async Task An_oversized_body_is_refused_before_it_is_parsed()
    {
        await using var fixture = await StartAsync(settings =>
            settings["agentsplice:limits:maxRequestBodyBytes"] = "512");

        var padding = new string('x', 4096);
        var body = $$"""{"model":"m","messages":[{"role":"user","content":"{{padding}}"}]}""";

        var error = await PostForErrorAsync(fixture, body, HttpStatusCode.RequestEntityTooLarge);

        Assert.Equal("agentsplice_invalid_request", error.GetProperty("code").GetString());
        Assert.Empty(fixture.Upstream.ReceivedRequests);
    }

    [Fact]
    public async Task A_body_within_the_limit_is_accepted()
    {
        await using var fixture = await StartAsync(settings =>
            settings["agentsplice:limits:maxRequestBodyBytes"] = "4096");
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture, Minimal());

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_refused_connection_is_reported_as_a_bad_gateway()
    {
        // Port 1 on loopback is reserved and never listening, so the connection is refused rather
        // than timing out.
        await using var fixture = await StartAsync(settings =>
            settings[GatewayFixture.RuntimeKey(0, "baseUrl")] = "http://127.0.0.1:1/v1");

        var error = await PostForErrorAsync(fixture, Minimal(), HttpStatusCode.BadGateway);

        Assert.Equal("agentsplice_runtime_unavailable", error.GetProperty("code").GetString());
        Assert.Equal("upstream_unavailable_error", error.GetProperty("type").GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task An_upstream_credential_rejection_is_not_echoed_to_the_client(HttpStatusCode status)
    {
        // The credential is the gateway's. Echoing 401 would tell a client to fix a key it does not
        // own, and the upstream body can hint at the key's shape.
        const string RevealingBody = """{"error":{"message":"Invalid API key sk-abc123..."}}""";

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.Json(RevealingBody, (int)status));

        using var response = await PostAsync(fixture, Minimal());
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.DoesNotContain("sk-abc123", payload, StringComparison.Ordinal);
        Assert.Contains("agentsplice_runtime_authentication_failed", payload, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_upstream_4xx_is_relayed_verbatim()
    {
        // The runtime's own message is the most actionable diagnostic a user has; replacing it with
        // a gateway error would discard it (ADR 0008 decision 12).
        const string UpstreamError =
            """{"error":{"message":"This model does not support tools.","type":"invalid_request_error"}}""";

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(UpstreamError, 400));

        using var response = await PostAsync(fixture, Minimal());

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(UpstreamError, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task An_upstream_429_is_relayed_with_its_retry_after()
    {
        // A 429 without Retry-After has discarded the one thing the status exists to convey.
        await using var fixture = await StartAsync();

        var script = UpstreamResponseScripts.Json("""{"error":{"message":"model is loading"}}""", 429)
            with
        { Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Retry-After"] = "7" } };

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script);

        using var response = await PostAsync(fixture, Minimal());

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("7", Assert.Single(response.Headers.GetValues("Retry-After")));
    }

    [Fact]
    public async Task An_upstream_5xx_is_relayed_verbatim_rather_than_masked()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.Json("""{"error":{"message":"runtime exploded"}}""", 500));

        using var response = await PostAsync(fixture, Minimal());

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Contains("runtime exploded", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_non_json_upstream_error_is_still_relayed_verbatim()
    {
        // Parsing gathers evidence and never gates forwarding, so an HTML error page from an
        // interposed proxy still reaches the client as the runtime's answer.
        const string Html = "<html><body>Bad Gateway</body></html>";

        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Text(Html, "text/html", 502));

        using var response = await PostAsync(fixture, Minimal());

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(Html, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_relayed_error_still_carries_the_correlation_headers()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json("{}", 429));

        using var response = await PostAsync(fixture, Minimal());

        Assert.True(response.Headers.Contains("x-agentsplice-request-id"));
        Assert.True(response.Headers.Contains("x-agentsplice-exchange-id"));
        Assert.True(response.Headers.Contains("x-agentsplice-runtime"));
    }

    [Fact]
    public async Task A_malformed_upstream_success_is_a_protocol_error()
    {
        // A malformed 2xx is a protocol violation; a malformed error is still the runtime's answer.
        //
        // The runtime declares a length and then stops short of it, rather than resetting the
        // connection. Both are the same fault, but only this one is deterministic: a reset can
        // outrun the bytes that preceded it, and then the gateway never saw an answer at all — see
        // the sibling test below.
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.UnderDeliveredContentLength());

        var error = await PostForErrorAsync(fixture, Minimal(), HttpStatusCode.BadGateway);

        Assert.Equal("agentsplice_invalid_upstream_response", error.GetProperty("code").GetString());
        Assert.Equal("upstream_protocol_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_connection_reset_mid_body_never_reaches_the_client_as_a_success()
    {
        // A reset races the data that preceded it: the gateway either read a partial body, or the
        // reset arrived first and it never saw a response at all. Both are honest — the second
        // genuinely is "nothing was received" — so this asserts what is true either way rather than
        // pinning a code the transport decides. Pinning one is what made this test flaky.
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.TruncatedJson());

        var error = await PostForErrorAsync(fixture, Minimal(), HttpStatusCode.BadGateway);

        Assert.Contains(error.GetProperty("code").GetString(), ResetMidBodyCodes);
    }

    [Fact]
    public async Task An_oversized_upstream_body_is_refused()
    {
        await using var fixture = await StartAsync(settings =>
            settings["agentsplice:limits:maxUpstreamCompletionBodyBytes"] = "256");

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.Json($$"""{"padding":"{{new string('y', 4096)}}"}"""));

        var error = await PostForErrorAsync(fixture, Minimal(), HttpStatusCode.BadGateway);

        Assert.Equal("agentsplice_invalid_upstream_response", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_runtime_that_never_sends_headers_produces_a_gateway_timeout()
    {
        await using var fixture = await StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:00:00.250";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:00.250";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:30";
        });

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallBeforeHeaders(TimeSpan.FromSeconds(10)));

        var error = await PostForErrorAsync(fixture, Minimal(), HttpStatusCode.GatewayTimeout);

        Assert.Equal("agentsplice_upstream_timeout", error.GetProperty("code").GetString());
        Assert.Equal("upstream_timeout_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_runtime_that_answers_then_stalls_produces_a_gateway_timeout()
    {
        // Validation guarantees responseHeaders never exceeds total, so the total budget can only
        // fire distinctly once headers have arrived and the body read is what stalls.
        await using var fixture = await StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "timeouts:connect")] = "00:00:00.500";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:00:00.500";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:00.500";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:00.500";
        });

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallAfterHeaders(TimeSpan.FromSeconds(10)));

        var error = await PostForErrorAsync(fixture, Minimal(), HttpStatusCode.GatewayTimeout);

        Assert.Equal("agentsplice_upstream_timeout", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task A_timeout_is_never_reported_as_an_unreachable_runtime()
    {
        // A runtime that accepted the connection and then went quiet is a different defect from one
        // that refused it, and collapsing the two makes the real cause unattributable.
        await using var fixture = await StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "timeouts:responseHeaders")] = "00:00:00.250";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:idleStream")] = "00:00:00.250";
            settings[GatewayFixture.RuntimeKey(0, "timeouts:total")] = "00:00:30";
        });

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.StallBeforeHeaders(TimeSpan.FromSeconds(10)));

        var error = await PostForErrorAsync(fixture, Minimal(), HttpStatusCode.GatewayTimeout);

        Assert.NotEqual("agentsplice_runtime_unavailable", error.GetProperty("code").GetString());
    }

    [Fact]
    public async Task No_error_message_reveals_where_the_gateway_was_connecting()
    {
        await using var fixture = await StartAsync(settings =>
            settings[GatewayFixture.RuntimeKey(0, "baseUrl")] = "http://127.0.0.1:1/v1");

        using var response = await PostAsync(fixture, Minimal());
        var payload = await response.Content.ReadAsStringAsync();

        Assert.DoesNotContain("127.0.0.1", payload, StringComparison.Ordinal);
        Assert.DoesNotContain("http://", payload, StringComparison.Ordinal);
    }

    private const string Completion = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}
        """;

    private static Task<GatewayFixture> StartAsync(Action<Dictionary<string, string?>>? configure = null) =>
        GatewayFixture.StartAsync(settings =>
        {
            settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            configure?.Invoke(settings);
        });

    private static string Minimal() => """{"model":"m","messages":[{"role":"user","content":"hi"}]}""";

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture fixture, string body) =>
        fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            new StringContent(body, Encoding.UTF8, "application/json"));

    private static async Task<JsonElement> PostForErrorAsync(
        GatewayFixture fixture,
        string body,
        HttpStatusCode expected)
    {
        using var response = await PostAsync(fixture, body);
        var payload = await response.Content.ReadAsStringAsync();

        Assert.Equal(expected, response.StatusCode);

        using var document = JsonDocument.Parse(payload);

        return document.RootElement.GetProperty("error").Clone();
    }
}
