using System.Net;
using System.Text;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// Trace correlation across the response surface (FR-OBS-001, docs/API.md "Gateway headers").
/// </summary>
/// <remarks>
/// These would all have passed vacuously before Stage 1A registered its own
/// <c>ActivityListener</c>: with no listener, <c>StartActivity</c> returns null and the header is
/// simply absent. That is exactly the failure mode worth pinning.
/// </remarks>
public sealed class TraceCorrelationTests
{
    private const string Completion = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}
        """;

    [Fact]
    public async Task A_completion_returns_a_trace_identifier()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture);

        Assert.True(
            response.Headers.TryGetValues("x-agentsplice-trace-id", out var values),
            "The trace header is absent, which means no activity was created.");
        Assert.NotEmpty(Assert.Single(values!));
    }

    [Fact]
    public async Task The_trace_identifier_is_thirty_two_lowercase_hex_characters()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture);
        var traceId = response.Headers.GetValues("x-agentsplice-trace-id").Single();

        Assert.Equal(32, traceId.Length);
        Assert.All(traceId, character => Assert.True(
            char.IsAsciiDigit(character) || character is >= 'a' and <= 'f',
            $"'{traceId}' is not lowercase hexadecimal."));
    }

    [Fact]
    public async Task The_trace_identifier_is_never_all_zero()
    {
        // The W3C all-zero identifier means "no trace". Emitting it would look like correlation
        // while carrying none.
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture);

        Assert.NotEqual(
            new string('0', 32),
            response.Headers.GetValues("x-agentsplice-trace-id").Single());
    }

    [Fact]
    public async Task Two_requests_receive_distinct_trace_identifiers()
    {
        await using var fixture = await StartAsync();
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var first = await PostAsync(fixture);
        using var second = await PostAsync(fixture);

        Assert.NotEqual(
            first.Headers.GetValues("x-agentsplice-trace-id").Single(),
            second.Headers.GetValues("x-agentsplice-trace-id").Single());
    }

    [Fact]
    public async Task An_error_response_still_carries_a_trace_identifier()
    {
        await using var fixture = await StartAsync();

        using var response = await PostAsync(fixture, "not json at all");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.Contains("x-agentsplice-trace-id"));
    }

    private static Task<GatewayFixture> StartAsync() =>
        GatewayFixture.StartAsync(settings =>
        {
            settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
        });

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture fixture, string? body = null) =>
        fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            new StringContent(
                body ?? """{"model":"m","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"));
}
