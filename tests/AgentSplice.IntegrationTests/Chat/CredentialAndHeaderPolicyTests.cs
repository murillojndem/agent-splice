using System.Net.Http.Headers;
using System.Text;
using AgentSplice.IntegrationTests.Hosting;
using AgentSplice.TestSupport.FakeUpstream;
using Xunit;

namespace AgentSplice.IntegrationTests.Chat;

/// <summary>
/// Credential containment and the header allowlists (docs/SECURITY.md, FR-DATA-010).
/// </summary>
public sealed class CredentialAndHeaderPolicyTests : IDisposable
{
    private const string ApiKeyVariable = "AGENTSPLICE_TEST_UPSTREAM_KEY";
    private const string ApiKeyValue = "sk-test-do-not-leak-4f2a";

    private const string Completion = """
        {"id":"chatcmpl-1","object":"chat.completion","model":"m",
         "choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}
        """;

    public CredentialAndHeaderPolicyTests() =>
        Environment.SetEnvironmentVariable(ApiKeyVariable, ApiKeyValue);

    public void Dispose() => Environment.SetEnvironmentVariable(ApiKeyVariable, null);

    [Fact]
    public async Task The_configured_key_is_attached_as_a_bearer_token()
    {
        await using var fixture = await StartAsync(withKey: true);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        await PostAsync(fixture);

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.Equal("Bearer " + ApiKeyValue, Assert.Single(recorded.Headers["Authorization"]));
    }

    [Fact]
    public async Task No_authorization_header_is_sent_when_no_key_is_configured()
    {
        // An empty bearer token would be rejected upstream and reported as an authentication
        // failure whose real cause is an unset variable.
        await using var fixture = await StartAsync(withKey: false);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        await PostAsync(fixture);

        Assert.False(Assert.Single(fixture.Upstream.ReceivedRequests).Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task The_clients_own_credential_never_replaces_the_runtimes()
    {
        await using var fixture = await StartAsync(withKey: true);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var request = Post();
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", "client-token-should-not-travel");

        await fixture.Client.SendAsync(request);

        var authorization = Assert.Single(Assert.Single(fixture.Upstream.ReceivedRequests).Headers["Authorization"]);

        Assert.Equal("Bearer " + ApiKeyValue, authorization);
        Assert.DoesNotContain("client-token-should-not-travel", authorization, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_never_appears_in_a_client_response()
    {
        await using var fixture = await StartAsync(withKey: true);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var response = await PostAsync(fixture);
        var payload = await response.Content.ReadAsStringAsync();
        var headers = string.Join("|", response.Headers.Select(header => $"{header.Key}:{string.Join(",", header.Value)}"));

        Assert.DoesNotContain(ApiKeyValue, payload, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKeyValue, headers, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_key_never_appears_in_an_error_response()
    {
        await using var fixture = await StartAsync(withKey: true);
        fixture.Upstream.EnqueueFor(
            "/v1/chat/completions",
            UpstreamResponseScripts.Json($$$"""{"error":{"message":"bad key {{{ApiKeyValue}}}"}}""", 401));

        using var response = await PostAsync(fixture);

        Assert.DoesNotContain(
            ApiKeyValue,
            await response.Content.ReadAsStringAsync(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_arbitrary_client_header_is_not_forwarded_upstream()
    {
        // Allowlisted, not denylisted: a header a client invents must not cross the boundary just
        // because nobody thought to block it.
        await using var fixture = await StartAsync(withKey: false);
        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var request = Post();
        request.Headers.Add("x-client-invented", "should-not-travel");
        request.Headers.Add("cookie", "session=abc123");

        await fixture.Client.SendAsync(request);

        var recorded = Assert.Single(fixture.Upstream.ReceivedRequests);
        Assert.False(recorded.Headers.ContainsKey("x-client-invented"));
        Assert.False(recorded.Headers.ContainsKey("cookie"));
    }

    [Fact]
    public async Task An_arbitrary_upstream_header_is_not_relayed_to_the_client()
    {
        await using var fixture = await StartAsync(withKey: false);

        var script = UpstreamResponseScripts.Json(Completion) with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-runtime-internal"] = "slot-7",
                ["set-cookie"] = "runtime-session=xyz",
            },
        };

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script);

        using var response = await PostAsync(fixture);

        Assert.False(response.Headers.Contains("x-runtime-internal"));
        Assert.False(response.Headers.Contains("set-cookie"));
    }

    [Fact]
    public async Task A_rate_limit_header_is_relayed_because_a_client_can_act_on_it()
    {
        await using var fixture = await StartAsync(withKey: false);

        var script = UpstreamResponseScripts.Json(Completion) with
        {
            Headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["x-ratelimit-remaining-requests"] = "3",
            },
        };

        fixture.Upstream.EnqueueFor("/v1/chat/completions", script);

        using var response = await PostAsync(fixture);

        Assert.Equal("3", Assert.Single(response.Headers.GetValues("x-ratelimit-remaining-requests")));
    }

    private static Task<GatewayFixture> StartAsync(bool withKey) =>
        GatewayFixture.StartAsync(settings =>
        {
            settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            settings[GatewayFixture.RuntimeKey(0, "apiKeyEnvironmentVariable")] =
                withKey ? ApiKeyVariable : null;
        });

    private static HttpRequestMessage Post() =>
        new(HttpMethod.Post, new Uri("/v1/chat/completions", UriKind.Relative))
        {
            Content = new StringContent(
                """{"model":"m","messages":[{"role":"user","content":"hi"}]}""",
                Encoding.UTF8,
                "application/json"),
        };

    private static Task<HttpResponseMessage> PostAsync(GatewayFixture fixture) =>
        fixture.Client.SendAsync(Post());
}
