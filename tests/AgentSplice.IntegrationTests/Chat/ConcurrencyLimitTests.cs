using System.Net;
using System.Text;
using System.Text.Json;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// The bound on how many completions the gateway serves at once (docs/SECURITY.md).
/// </summary>
/// <remarks>
/// The limit is worth having only if a refused request costs nothing. A gateway that read the body,
/// resolved the model, and opened an upstream connection before refusing would spend most of the
/// work it was trying to avoid, so these tests assert what the runtime saw as much as what the
/// client received.
/// </remarks>
public sealed class ConcurrencyLimitTests
{
    private static readonly TimeSpan WaitBudget = TimeSpan.FromSeconds(20);

    [Fact]
    public async Task A_request_above_the_limit_is_refused_with_the_stable_code()
    {
        var gate = new UpstreamGate();

        await using var fixture = await StartAsync();

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create()
                .Data("""{"choices":[{"delta":{"content":"hi"}}]}""")
                .Gate(gate)
                .Done()
                .Build());

        // Held open: the first completion occupies the only permit until the gate is released.
        using var held = await SendAsync(fixture);

        await gate.WaitForReachedAsync(WaitBudget);

        using var refused = await SendAsync(fixture);

        Assert.Equal(HttpStatusCode.TooManyRequests, refused.StatusCode);
        Assert.Equal("agentsplice_gateway_overloaded", await ErrorCodeAsync(refused));
        Assert.Equal("rate_limit_error", await ErrorTypeAsync(refused));

        // Without this the status says "slow down" and gives a client nothing to slow down by, so
        // it retries immediately and the refusal accomplishes nothing.
        Assert.NotNull(refused.Headers.RetryAfter);

        // Only the held request reached the runtime.
        Assert.Single(fixture.Upstream.ReceivedRequests);

        gate.Release();
        await held.Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task A_refused_request_still_carries_its_correlation_token()
    {
        // A refusal is the response a user is most likely to report and least able to correlate
        // without this.
        var gate = new UpstreamGate();

        await using var fixture = await StartAsync();

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data("""{"choices":[{"delta":{"content":"hi"}}]}""").Gate(gate).Done().Build());

        using var held = await SendAsync(fixture);
        await gate.WaitForReachedAsync(WaitBudget);

        using var refused = await SendAsync(fixture);

        Assert.True(refused.Headers.Contains("x-agentsplice-request-id"));

        gate.Release();
        await held.Content.ReadAsByteArrayAsync();
    }

    [Fact]
    public async Task Model_discovery_stays_answerable_while_completions_are_saturated()
    {
        // The bound covers completions alone. Discovery has to answer exactly when the gateway is
        // full, because that is when an operator most needs to ask what it is doing.
        var gate = new UpstreamGate();

        await using var fixture = await StartAsync();

        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            SseScript.Create().Data("""{"choices":[{"delta":{"content":"hi"}}]}""").Gate(gate).Done().Build());

        using var held = await SendAsync(fixture);
        await gate.WaitForReachedAsync(WaitBudget);

        using var models = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, models.StatusCode);

        gate.Release();
        await held.Content.ReadAsByteArrayAsync();
    }

    private static async Task<string?> ErrorCodeAsync(HttpResponseMessage response) =>
        (await ErrorAsync(response)).GetProperty("code").GetString();

    private static async Task<string?> ErrorTypeAsync(HttpResponseMessage response) =>
        (await ErrorAsync(response)).GetProperty("type").GetString();

    private static async Task<JsonElement> ErrorAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());

        return document.RootElement.GetProperty("error").Clone();
    }

    private static Task<GatewayFixture> StartAsync() =>
        GatewayFixture.StartAsync(settings =>
        {
            settings["agentsplice:limits:maxConcurrentCompletions"] = "1";
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
        });

    private static async Task<HttpResponseMessage> SendAsync(GatewayFixture fixture)
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

        return await fixture.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    }
}
