using System.Net;
using System.Text.Json;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Models;

/// <summary>
/// <c>GET /v1/models</c> end to end, through the production host against a real fake upstream
/// (docs/SPECIFICATION.md FR-MOD-001 to FR-MOD-004).
/// </summary>
public sealed class ListModelsEndpointTests
{
    private const string CatalogueWithTwoModels = """
        {
          "object": "list",
          "data": [
            { "id": "qwen3.6-27b-mtp", "object": "model", "created": 1700000000, "owned_by": "organization_owner" },
            { "id": "phi-4", "object": "model", "created": 1700000001, "owned_by": "organization_owner" }
          ]
        }
        """;

    [Fact]
    public async Task Discovered_models_are_listed_in_the_openai_shape()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("list", document.RootElement.GetProperty("object").GetString());
        Assert.Equal(["qwen3.6-27b-mtp", "phi-4"], Ids(document));
    }

    [Fact]
    public async Task Every_listed_model_carries_the_fields_the_openai_schema_requires()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        foreach (var model in document.RootElement.GetProperty("data").EnumerateArray())
        {
            Assert.Equal("model", model.GetProperty("object").GetString());
            Assert.Equal(JsonValueKind.Number, model.GetProperty("created").ValueKind);
            Assert.Equal(JsonValueKind.String, model.GetProperty("owned_by").ValueKind);
        }
    }

    [Fact]
    public async Task A_discovered_model_passes_through_the_creation_time_the_runtime_reported()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        var first = document.RootElement.GetProperty("data")[0];
        Assert.Equal(1_700_000_000, first.GetProperty("created").GetInt64());
        Assert.Equal("organization_owner", first.GetProperty("owned_by").GetString());
    }

    [Fact]
    public async Task Configured_aliases_are_listed_alongside_discovered_models()
    {
        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
        });

        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(["local-coder", "qwen3.6-27b-mtp", "phi-4"], Ids(document));
    }

    [Fact]
    public async Task An_alias_inherits_the_creation_evidence_of_the_model_it_targets()
    {
        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
        });

        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        var alias = document.RootElement.GetProperty("data")[0];
        Assert.Equal("local-coder", alias.GetProperty("id").GetString());
        Assert.Equal(1_700_000_000, alias.GetProperty("created").GetInt64());
    }

    [Fact]
    public async Task An_alias_with_no_creation_evidence_emits_the_compatibility_sentinel()
    {
        // The OpenAI schema requires a present integer, so the envelope substitutes zero. The
        // catalogue itself still holds null; this is the only place the sentinel exists.
        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings[GatewayFixture.AliasKey(0, "id")] = "local-coder";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
        });

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(0, document.RootElement.GetProperty("data")[0].GetProperty("created").GetInt64());
        Assert.Empty(fixture.Upstream.ReceivedRequests);
    }

    [Fact]
    public async Task Discovery_is_cached_so_a_second_call_does_not_contact_the_runtime()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var first = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        using var second = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Single(fixture.Upstream.ReceivedRequests);
    }

    [Fact]
    public async Task Discovery_requests_the_models_path_beneath_the_configured_prefix()
    {
        // The configured base URL ends in /v1, which Uri resolution would otherwise discard.
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.Equal("GET", recorded.Method);
        Assert.Equal("/v1/models", recorded.Path);
    }

    [Fact]
    public async Task A_runtime_that_answers_with_no_models_produces_an_empty_list()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor(
            "/v1/models",
            UpstreamResponseScripts.Json("""{ "object": "list", "data": [] }"""));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(document.RootElement.GetProperty("data").EnumerateArray());
    }

    [Fact]
    public async Task A_total_discovery_failure_with_nothing_known_is_reported_as_a_bad_gateway()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.SetDefault(UpstreamResponseScripts.Status((int)HttpStatusCode.InternalServerError));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(
            "agentsplice_runtime_unavailable",
            document.RootElement.GetProperty("error").GetProperty("code").GetString());
        Assert.Equal(
            "upstream_unavailable_error",
            document.RootElement.GetProperty("error").GetProperty("type").GetString());
    }

    [Fact]
    public async Task An_empty_configuration_returns_an_empty_list_rather_than_blaming_the_upstream()
    {
        // Nothing configured is an operator fact. Reporting it as an upstream outage would send a
        // user looking in the wrong place.
        await using var fixture = await GatewayFixture.StartAsync(settings =>
            settings[GatewayFixture.RuntimeKey(0, "enabled")] = "false");

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty(document.RootElement.GetProperty("data").EnumerateArray());
        Assert.Empty(fixture.Upstream.ReceivedRequests);
    }

    [Fact]
    public async Task A_malformed_upstream_catalogue_is_reported_as_a_bad_gateway()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.MalformedJson());

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public async Task A_model_entry_the_runtime_reported_malformed_is_skipped_rather_than_failing_discovery()
    {
        // One unusable model should cost one model, not the whole inventory.
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json("""
            {
              "object": "list",
              "data": [
                { "object": "model" },
                { "id": "usable-model", "object": "model" }
              ]
            }
            """));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(["usable-model"], Ids(document));
    }

    [Fact]
    public async Task Unknown_fields_on_an_upstream_model_do_not_break_discovery()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json("""
            {
              "object": "list",
              "data": [
                { "id": "future-model", "object": "model", "quantization": "q4_k_m", "arch": "llama" }
              ]
            }
            """));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));
        var document = await ReadJsonAsync(response);

        Assert.Equal(["future-model"], Ids(document));
    }

    [Fact]
    public async Task Every_response_carries_a_request_identifier()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        Assert.True(response.Headers.TryGetValues("x-agentsplice-request-id", out var values));
        Assert.NotEmpty(Assert.Single(values!));
    }

    [Fact]
    public async Task An_error_response_carries_a_request_identifier_too()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.SetDefault(UpstreamResponseScripts.Status((int)HttpStatusCode.InternalServerError));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.True(response.Headers.Contains("x-agentsplice-request-id"));
    }

    [Fact]
    public async Task A_client_supplied_request_identifier_is_echoed()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/v1/models", UriKind.Relative));
        request.Headers.Add("x-request-id", "caller-supplied-1");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal("caller-supplied-1", response.Headers.GetValues("x-agentsplice-request-id").Single());
    }

    [Fact]
    public async Task Model_discovery_returns_no_exchange_identifier()
    {
        // Listing models is not a completion exchange, and returning an identifier for a record that
        // does not exist would be a fabrication.
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        using var response = await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        Assert.False(response.Headers.Contains("x-agentsplice-exchange-id"));
    }

    [Fact]
    public async Task Discovery_does_not_send_an_authorization_header_when_no_key_is_configured()
    {
        await using var fixture = await GatewayFixture.StartAsync();
        fixture.Upstream.EnqueueFor("/v1/models", UpstreamResponseScripts.Json(CatalogueWithTwoModels));

        await fixture.Client.GetAsync(new Uri("/v1/models", UriKind.Relative));

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.False(recorded.Headers.ContainsKey("Authorization"));
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync());

    private static string[] Ids(JsonDocument document) =>
    [
        .. document.RootElement
            .GetProperty("data")
            .EnumerateArray()
            .Select(model => model.GetProperty("id").GetString()!),
    ];
}
