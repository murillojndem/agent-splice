using System.Net;
using System.Text.Json;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Administration;

/// <summary>
/// Readiness performs no runtime I/O unless a deployment asked it to (FR-HEALTH-003).
/// </summary>
/// <remarks>
/// Driven by a gate rather than a timeout. A test that waited for a real connect timeout would be
/// slow when it passed and misleading when it failed — the whole defect was that readiness could
/// block on a runtime, so proving it does not has to mean proving it never asks. The gate is held
/// shut for the duration: if readiness consults discovery it cannot return, and the assertion is that
/// it returns anyway.
/// </remarks>
public sealed class ReadinessIsolationTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Readiness_answers_without_asking_a_runtime_anything_by_default()
    {
        // The runtime is scripted to stall on every model listing. With the option off, readiness
        // must not reach it — and if it does, this completes only when the stall expires, which is
        // far outside the bound.
        await using var fixture = await GatewayFixture.StartAsync(settings =>
            settings[GatewayFixture.RuntimeKey(0, "discovery:cacheDuration")] = "00:00:00");

        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.StallBeforeHeaders(Bound * 6));

        using var cancellation = new CancellationTokenSource(Bound);

        using var response = await fixture.Client.GetAsync(
            new Uri("/health/ready", UriKind.Relative),
            cancellation.Token);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var readiness = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellation.Token)).RootElement;

        Assert.True(readiness.GetProperty("ready").GetBoolean());
        Assert.False(readiness.GetProperty("requiresReachableRuntime").GetBoolean());

        // Absent rather than zero. Zero would report that every runtime was found unreachable, from
        // a check that did not happen.
        Assert.False(readiness.TryGetProperty("reachableRuntimes", out _));

        // The decisive assertion: nothing was asked. A readiness answer that reached the runtime and
        // happened to be fast is not the property under test.
        Assert.DoesNotContain(
            fixture.Upstream.ReceivedRequests,
            request => request.Path.Contains("/models", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Readiness_consults_the_same_runtime_when_a_deployment_asks_it_to()
    {
        // The other half. Without this, the test above would also pass on a readiness endpoint that
        // never consulted anything under any configuration.
        await using var fixture = await GatewayFixture.StartAsync(settings =>
            settings["agentsplice:health:requireReachableRuntime"] = "true");

        fixture.Upstream.EnqueueFor(
            "/v1/models",
            UpstreamResponseScripts.Json(
                """{"object":"list","data":[{"id":"qwen3.6-27b-mtp","object":"model","owned_by":"local"}]}"""));

        using var response = await fixture.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var readiness = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.True(readiness.GetProperty("ready").GetBoolean());
        Assert.Equal(1, readiness.GetProperty("reachableRuntimes").GetInt32());

        Assert.Contains(
            fixture.Upstream.ReceivedRequests,
            request => request.Path.Contains("/models", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Liveness_asks_a_runtime_nothing_under_either_setting()
    {
        // FR-HEALTH-002 is unconditional: liveness never depends on upstream availability, so the
        // flag that changes readiness must not change this.
        await using var fixture = await GatewayFixture.StartAsync(settings =>
            settings["agentsplice:health:requireReachableRuntime"] = "true");

        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.StallBeforeHeaders(Bound * 6));

        using var cancellation = new CancellationTokenSource(Bound);

        using var response = await fixture.Client.GetAsync(
            new Uri("/health/live", UriKind.Relative),
            cancellation.Token);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.DoesNotContain(
            fixture.Upstream.ReceivedRequests,
            request => request.Path.Contains("/models", StringComparison.Ordinal));
    }
}
