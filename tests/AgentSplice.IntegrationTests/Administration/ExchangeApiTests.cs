using System.Net;
using System.Text;
using System.Text.Json;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Administration;

/// <summary>
/// What an operator can actually read back (FR-DASH-001, FR-TRACE-009, FR-TRACE-010).
/// </summary>
/// <remarks>
/// Driven end to end: a real request through the real gateway into a real SQLite file, then read
/// through the published endpoints. The Stage 1C exit criterion is that a user can inspect what was
/// sent structurally, where time was spent, and how the exchange ended, and only a test that goes the
/// whole way can say whether that is true.
/// </remarks>
public sealed class ExchangeApiTests
{
    private const string Completion = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"qwen3.6-27b-mtp",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hello"},"finish_reason":"stop"}],
         "usage":{"prompt_tokens":41,"completion_tokens":7,"total_tokens":48}}
        """;

    private static readonly TimeSpan WriteTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task A_proxied_exchange_is_listed_with_its_routing_and_outcome()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        await ProxyAsync(fixture);

        var page = await WaitForPageAsync(fixture);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal("completed", item.GetProperty("status").GetString());
        Assert.Equal("qwen3.6-27b-mtp", item.GetProperty("clientModelId").GetString());
        Assert.Equal(GatewayFixture.RuntimeId, item.GetProperty("runtimeId").GetString());
        Assert.False(item.GetProperty("streaming").GetBoolean());

        // The value FR-TRACE-010 exists to make readable, and the one that was wrong on every row
        // until the store started stamping it.
        Assert.Equal("metadata_only", item.GetProperty("contentRetentionState").GetString());

        // Present and null rather than absent, because "there are no more" is an answer.
        Assert.Equal(JsonValueKind.Null, page.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task Exchange_detail_carries_measurements_with_their_provenance()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        await ProxyAsync(fixture);

        var id = (await WaitForPageAsync(fixture))
            .GetProperty("items")[0]
            .GetProperty("exchangeId")
            .GetString();

        var detail = await ReadJsonAsync(fixture, "/api/v1/exchanges/" + id);
        var measurements = detail.GetProperty("measurements").EnumerateArray().ToList();

        Assert.NotEmpty(measurements);

        // Every measurement states where it came from. A histogram records a number; this surface has
        // to record a number and its provenance, or a replay comparison later treats an estimate and
        // a clock reading as equally trustworthy (P-008).
        Assert.All(measurements, measurement =>
            Assert.False(string.IsNullOrEmpty(measurement.GetProperty("provenance").GetString())));

        var prompt = measurements.Single(m => m.GetProperty("name").GetString() == "usage.prompt.tokens");
        Assert.Equal("upstream_reported", prompt.GetProperty("provenance").GetString());

        var total = measurements.Single(m => m.GetProperty("name").GetString() == "exchange.total.duration");
        Assert.Equal("measured", total.GetProperty("provenance").GetString());

        Assert.Equal(200, detail.GetProperty("upstreamStatusCode").GetInt32());
        Assert.Equal("open_ai_chat_completions", detail.GetProperty("ingressProtocol").GetString());
    }

    [Fact]
    public async Task The_structural_summary_is_served_as_the_document_that_was_stored()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        await ProxyAsync(fixture);

        var id = (await WaitForPageAsync(fixture)).GetProperty("items")[0].GetProperty("exchangeId").GetString();
        var detail = await ReadJsonAsync(fixture, "/api/v1/exchanges/" + id);

        var summary = detail.GetProperty("structuralSummary");

        Assert.Equal(1, summary.GetProperty("messageCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("messageCountsByRole").GetProperty("user").GetInt32());

        // Never the prompt. The summary answers "what did the client send" in shapes and counts.
        Assert.DoesNotContain("hi", summary.GetRawText(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_timeline_is_readable_in_sequence_order_and_ends_where_the_store_stamped_it()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        await ProxyAsync(fixture);

        var id = (await WaitForPageAsync(fixture)).GetProperty("items")[0].GetProperty("exchangeId").GetString();

        var timeline = await WaitForAsync(
            fixture,
            "/api/v1/exchanges/" + id + "/timeline",
            document => document.EnumerateArray()
                .Any(o => o.GetProperty("type").GetString() == "persistence_completed"));

        var types = timeline.EnumerateArray().Select(o => o.GetProperty("type").GetString()).ToList();
        var sequences = timeline.EnumerateArray().Select(o => o.GetProperty("sequence").GetInt32()).ToList();

        Assert.Equal("request_accepted", types[0]);
        Assert.Equal("metadata_queued", types[^2]);
        Assert.Equal("persistence_completed", types[^1]);
        Assert.Equal(sequences.OrderBy(sequence => sequence), sequences);
    }

    [Fact]
    public async Task The_observations_route_answers_the_same_evidence_as_the_timeline()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        await ProxyAsync(fixture);

        var id = (await WaitForPageAsync(fixture)).GetProperty("items")[0].GetProperty("exchangeId").GetString();

        var timeline = await ReadJsonAsync(fixture, "/api/v1/exchanges/" + id + "/timeline");
        var observations = await ReadJsonAsync(fixture, "/api/v1/exchanges/" + id + "/observations");

        Assert.Equal(timeline.GetRawText(), observations.GetRawText());
    }

    [Fact]
    public async Task An_unknown_or_unparsable_identifier_answers_the_same_way()
    {
        // Separating them would tell anyone probing this surface which of their guesses were
        // well-formed, and a caller who cannot see the store cannot tell the two apart anyway.
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        foreach (var candidate in new[] { Guid.NewGuid().ToString(), "not-a-guid" })
        {
            using var response = await fixture.Client.GetAsync(
                new Uri("/api/v1/exchanges/" + candidate, UriKind.Relative));

            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

            var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;

            Assert.Equal("agentsplice_exchange_not_found", body.GetProperty("error").GetProperty("code").GetString());
        }
    }

    [Theory]
    [InlineData("status=not_a_status", "status")]
    [InlineData("runtimeId=has spaces", "runtimeId")]
    [InlineData("limit=0", "limit")]
    [InlineData("limit=201", "limit")]
    [InlineData("cursor=not-base64!!", "cursor")]
    public async Task An_unusable_filter_is_refused_rather_than_ignored(string query, string parameter)
    {
        // A filter that is silently dropped returns a page that looks like an answer to the question
        // the caller asked and is an answer to a different one.
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        using var response = await fixture.Client.GetAsync(
            new Uri("/api/v1/exchanges?" + query, UriKind.Relative));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("error");

        Assert.Equal("agentsplice_invalid_query", error.GetProperty("code").GetString());
        Assert.Equal(parameter, error.GetProperty("param").GetString());
    }

    [Fact]
    public async Task A_page_resumes_from_its_cursor_without_skipping_or_repeating()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        for (var index = 0; index < 3; index++)
        {
            await ProxyAsync(fixture);
        }

        var first = await WaitForAsync(
            fixture,
            "/api/v1/exchanges?limit=2",
            document => document.GetProperty("items").GetArrayLength() == 2);

        var cursor = first.GetProperty("nextCursor").GetString();
        Assert.NotNull(cursor);

        var second = await ReadJsonAsync(
            fixture,
            "/api/v1/exchanges?limit=2&cursor=" + Uri.EscapeDataString(cursor!));

        var firstIds = Ids(first);
        var secondIds = Ids(second);

        Assert.Equal(2, firstIds.Count);
        Assert.Single(secondIds);
        Assert.Empty(firstIds.Intersect(secondIds, StringComparer.Ordinal));
        Assert.Equal(JsonValueKind.Null, second.GetProperty("nextCursor").ValueKind);
    }

    [Fact]
    public async Task A_status_filter_selects_only_that_state()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        await ProxyAsync(fixture);
        await RefuseAsync(fixture);

        await WaitForAsync(fixture, "/api/v1/exchanges", document => document.GetProperty("items").GetArrayLength() == 2);

        var failed = await ReadJsonAsync(fixture, "/api/v1/exchanges?status=failed");
        var item = Assert.Single(failed.GetProperty("items").EnumerateArray());

        Assert.Equal("failed", item.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_deployment_that_retains_nothing_says_so_rather_than_returning_an_empty_page()
    {
        // FR-DATA-001 makes ephemeral operation supported, so "no exchanges are stored" and "no
        // exchanges happened" are both true of such a deployment and only one answers the question.
        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings["agentsplice:persistence:mode"] = "None";
        });

        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/exchanges", UriKind.Relative));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);

        var error = JsonDocument.Parse(await response.Content.ReadAsStringAsync())
            .RootElement
            .GetProperty("error");

        Assert.Equal("agentsplice_persistence_disabled", error.GetProperty("code").GetString());
        Assert.Equal("configuration_error", error.GetProperty("type").GetString());
    }

    [Fact]
    public async Task A_request_refused_before_its_envelope_was_read_is_listed_without_a_model()
    {
        using var store = new TemporaryMetadataStore();
        await using var fixture = await StartAsync(store);

        await RefuseAsync(fixture);

        var page = await WaitForPageAsync(fixture);
        var item = Assert.Single(page.GetProperty("items").EnumerateArray());

        Assert.Equal("failed", item.GetProperty("status").GetString());

        // Null rather than absent or empty: the client never named a model AgentSplice could read,
        // and it never stated a streaming preference either.
        Assert.False(item.TryGetProperty("clientModelId", out _));
        Assert.Equal(JsonValueKind.Null, item.GetProperty("streaming").ValueKind);
    }

    private static List<string> Ids(JsonElement page) =>
        page.GetProperty("items")
            .EnumerateArray()
            .Select(item => item.GetProperty("exchangeId").GetString()!)
            .ToList();

    private static async Task<GatewayFixture> StartAsync(TemporaryMetadataStore store) =>
        await GatewayFixture.StartAsync(settings =>
        {
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings[GatewayFixture.AliasKey(0, "id")] = "qwen3.6-27b-mtp";
            settings[GatewayFixture.AliasKey(0, "runtimeId")] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.AliasKey(0, "upstreamModelId")] = "qwen3.6-27b-mtp";
            store.ApplyTo(settings);
        });

    private static async Task ProxyAsync(GatewayFixture fixture)
    {
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var content = new StringContent(
            """{"model":"qwen3.6-27b-mtp","messages":[{"role":"user","content":"hi"}]}""",
            Encoding.UTF8,
            "application/json");

        using var response = await fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            content);

        response.EnsureSuccessStatusCode();
    }

    private static async Task RefuseAsync(GatewayFixture fixture)
    {
        using var content = new StringContent("{ not json", Encoding.UTF8, "application/json");

        using var response = await fixture.Client.PostAsync(
            new Uri("/v1/chat/completions", UriKind.Relative),
            content);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    private static Task<JsonElement> WaitForPageAsync(GatewayFixture fixture) =>
        WaitForAsync(fixture, "/api/v1/exchanges", document => document.GetProperty("items").GetArrayLength() > 0);

    /// <summary>Polls a route until its body satisfies a condition, because the writer is asynchronous.</summary>
    private static async Task<JsonElement> WaitForAsync(
        GatewayFixture fixture,
        string route,
        Func<JsonElement, bool> satisfied)
    {
        var deadline = DateTime.UtcNow + WriteTimeout;
        var latest = default(JsonElement);

        while (DateTime.UtcNow < deadline)
        {
            latest = await ReadJsonAsync(fixture, route);

            if (satisfied(latest))
            {
                return latest;
            }

            await Task.Delay(25);
        }

        Assert.Fail(FormattableString.Invariant($"'{route}' did not reach the expected state within {WriteTimeout}."));

        return latest;
    }

    private static async Task<JsonElement> ReadJsonAsync(GatewayFixture fixture, string route)
    {
        using var response = await fixture.Client.GetAsync(new Uri(route, UriKind.Relative));

        response.EnsureSuccessStatusCode();

        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();
    }
}
