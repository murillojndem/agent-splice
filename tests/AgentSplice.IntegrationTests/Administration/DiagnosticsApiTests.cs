using System.Net;
using System.Text.Json;
using AgentSplice.IntegrationTests.Hosting;
using Xunit;

namespace AgentSplice.IntegrationTests.Administration;

/// <summary>
/// System information, runtimes, the administrative catalogue, and health
/// (FR-HEALTH-001 to FR-HEALTH-005, FR-MOD-009, FR-DASH-001).
/// </summary>
/// <remarks>
/// The assertions that matter are the ones about what is <em>not</em> claimed: a credential that does
/// not appear, a runtime that is not called healthy on no evidence, and a creation date that stays
/// absent rather than becoming 1970.
/// </remarks>
public sealed class DiagnosticsApiTests
{
    private const string ApiKeyVariable = "AGENTSPLICE_TEST_DIAGNOSTICS_KEY";

    [Fact]
    public async Task System_information_names_the_stage_and_what_is_retained()
    {
        await using var fixture = await GatewayFixture.StartAsync();

        var system = await ReadAsync(fixture, "/api/v1/system");

        Assert.Equal("1C", system.GetProperty("stage").GetString());
        Assert.NotEmpty(system.GetProperty("enabledModules").EnumerateArray());

        // Both false on a default test host: content capture is refused by this build, and the test
        // factory runs ephemeral.
        Assert.False(system.GetProperty("contentRetentionEnabled").GetBoolean());
        Assert.False(system.GetProperty("metadataRetentionEnabled").GetBoolean());
    }

    [Fact]
    public async Task A_runtime_is_described_by_the_name_of_its_credential_variable_and_never_its_value()
    {
        const string Secret = "SENTINEL-APIKEY-sk-diagnostics";

        Environment.SetEnvironmentVariable(ApiKeyVariable, Secret);

        try
        {
            await using var fixture = await GatewayFixture.StartAsync(settings =>
                settings[GatewayFixture.RuntimeKey(0, "apiKeyEnvironmentVariable")] = ApiKeyVariable);

            var runtimes = await ReadAsync(fixture, "/api/v1/runtimes");
            var runtime = Assert.Single(runtimes.EnumerateArray());

            Assert.Equal(GatewayFixture.RuntimeId, runtime.GetProperty("runtimeId").GetString());
            Assert.Equal(ApiKeyVariable, runtime.GetProperty("apiKeyEnvironmentVariable").GetString());
            Assert.True(runtime.GetProperty("enabled").GetBoolean());

            // The credential is resolved by the provider when it builds an upstream request and never
            // travels through routing, evidence, or this surface (FR-DATA-010).
            Assert.DoesNotContain("SENTINEL", runtimes.GetRawText(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(ApiKeyVariable, null);
        }
    }

    [Fact]
    public async Task The_administrative_catalogue_keeps_provenance_and_reports_absence_honestly()
    {
        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
        });

        var models = await ReadAsync(fixture, "/api/v1/models");
        var model = Assert.Single(models.EnumerateArray());

        Assert.Equal("local-coder", model.GetProperty("clientModelId").GetString());
        Assert.Equal("qwen3.6-27b-mtp", model.GetProperty("upstreamModelId").GetString());
        Assert.Equal("configured_alias", model.GetProperty("source").GetString());
        Assert.Equal("configured", model.GetProperty("capabilityProvenance").GetString());

        // Discovery is off, so nothing has ever asked this runtime whether it answers. Null rather
        // than false, which would read as unreachable for a runtime that is fully usable through its
        // aliases, and rather than true, which would be a claim from no evidence.
        Assert.Equal(JsonValueKind.Null, model.GetProperty("reachable").ValueKind);

        // Absent rather than zero. /v1/models emits 0 because the OpenAI schema demands a present
        // integer; that substitution belongs to that envelope alone (FR-DASH-006).
        Assert.False(model.TryGetProperty("created", out _));
    }

    [Fact]
    public async Task A_runtime_nothing_has_consulted_is_unknown_rather_than_healthy()
    {
        await using var fixture = await GatewayFixture.StartAsync(settings =>
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false");

        var health = await ReadAsync(fixture, "/api/v1/health/runtimes");
        var runtime = Assert.Single(health.EnumerateArray());

        Assert.Equal("unknown", runtime.GetProperty("status").GetString());

        // No timestamp, because there was no check. Inventing one would make "never consulted"
        // indistinguishable from "consulted and fine".
        Assert.False(runtime.TryGetProperty("checkedAt", out _));
    }

    [Fact]
    public async Task A_reachable_runtime_reports_healthy_with_the_moment_it_was_checked()
    {
        await using var fixture = await GatewayFixture.StartAsync();

        fixture.Upstream.EnqueueFor(
            "/v1/models",
            AgentSplice.TestSupport.FakeUpstream.UpstreamResponseScripts.Json(
                """{"object":"list","data":[{"id":"qwen3.6-27b-mtp","object":"model","owned_by":"local"}]}"""));

        var health = await ReadAsync(fixture, "/api/v1/health/runtimes");
        var runtime = Assert.Single(health.EnumerateArray());

        Assert.Equal("healthy", runtime.GetProperty("status").GetString());
        Assert.True(runtime.TryGetProperty("checkedAt", out _));
        Assert.False(runtime.GetProperty("servedFromStaleCache").GetBoolean());
    }

    [Fact]
    public async Task A_runtime_that_answers_with_no_models_is_not_reported_healthy()
    {
        // The state a naive health check calls healthy and an agent client experiences as a broken
        // deployment (FR-HEALTH-004).
        await using var fixture = await GatewayFixture.StartAsync();

        fixture.Upstream.EnqueueFor(
            "/v1/models",
            AgentSplice.TestSupport.FakeUpstream.UpstreamResponseScripts.Json("""{"object":"list","data":[]}"""));

        var health = await ReadAsync(fixture, "/api/v1/health/runtimes");

        Assert.Equal("no_models", Assert.Single(health.EnumerateArray()).GetProperty("status").GetString());
    }

    [Fact]
    public async Task Liveness_answers_without_consulting_anything()
    {
        // The fake upstream is scripted to 404 by default, so a liveness probe that consulted a
        // runtime would fail here. It must not.
        await using var fixture = await GatewayFixture.StartAsync();

        using var response = await fixture.Client.GetAsync(new Uri("/health/live", UriKind.Relative));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_satisfied_by_default_even_when_no_runtime_answers()
    {
        // A gateway whose runtime is down is still correctly configured and is still the component
        // able to report the outage. Reporting itself unready would have an orchestrator remove it.
        await using var fixture = await GatewayFixture.StartAsync();

        var readiness = await ReadAsync(fixture, "/health/ready");

        Assert.True(readiness.GetProperty("ready").GetBoolean());
        Assert.False(readiness.GetProperty("requiresReachableRuntime").GetBoolean());
    }

    [Fact]
    public async Task Readiness_fails_when_a_deployment_asked_for_a_reachable_runtime_and_has_none()
    {
        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings["agentsplice:health:requireReachableRuntime"] = "true";
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
        });

        using var response = await fixture.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var readiness = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

        Assert.False(readiness.GetProperty("ready").GetBoolean());
        Assert.Equal(0, readiness.GetProperty("reachableRuntimes").GetInt32());
        Assert.Equal(1, readiness.GetProperty("enabledRuntimes").GetInt32());
    }

    private static async Task<JsonElement> ReadAsync(GatewayFixture fixture, string route)
    {
        using var response = await fixture.Client.GetAsync(new Uri(route, UriKind.Relative));

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
