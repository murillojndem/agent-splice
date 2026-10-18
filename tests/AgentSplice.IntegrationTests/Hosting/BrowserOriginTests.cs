using System.Net;
using System.Text;
using AgentSplice.Application.Exchanges;
using AgentSplice.TestSupport.FakeUpstream;
using AgentSplice.TestSupport.Logging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// What a web page on another site can make the gateway do (ADR 0027, docs/THREAT_MODEL.md
/// "Browser-originated requests").
/// </summary>
/// <remarks>
/// Two attacks a loopback gateway is open to by default. A page that points its own domain at
/// <c>127.0.0.1</c> is same-origin with the gateway and can read what it answers; its requests still
/// carry its own domain as the <c>Host</c>, so only the loopback names are served. And any page can
/// post a form or plain text to the gateway without the browser asking first; a completion body must
/// be declared as JSON, which a page cannot send cross-site without a preflight the gateway never
/// answers.
/// </remarks>
public sealed class BrowserOriginTests
{
    private const string Completion =
        """{"id":"c1","choices":[{"index":0,"message":{"role":"assistant","content":"hi"},"finish_reason":"stop"}]}""";

    [Theory]
    [InlineData("localhost")]
    [InlineData("127.0.0.1:5280")]
    [InlineData("[::1]:5280")]
    public async Task A_request_addressed_to_a_loopback_name_is_served(string host)
    {
        await using var fixture = await GatewayFixture.StartAsync();

        using var response = await GetAsync(fixture, "/health/live", host);

        Assert.True(response.IsSuccessStatusCode, $"Host '{host}' answered {(int)response.StatusCode}.");
    }

    [Theory]
    [InlineData("/v1/models")]
    [InlineData("/api/v1/system")]
    [InlineData("/health/live")]
    [InlineData("/dashboard/")]
    public async Task A_request_addressed_to_another_name_is_refused_before_anything_answers(string path)
    {
        // What a rebinding page sends: its own domain, now resolving to the loopback address.
        await using var fixture = await GatewayFixture.StartAsync();

        using var response = await GetAsync(fixture, path, "attacker.example");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync());
        Assert.Empty(fixture.Upstream.ReceivedRequests);
    }

    [Fact]
    public async Task A_refused_name_is_logged_where_the_shipped_configuration_shows_it()
    {
        // The refusal is a 400 with no body, so the log is the one place that says why. The shipped
        // appsettings.json lifts the host filter's category to Information for this line alone.
        var logs = new CapturingLoggerProvider();

        await using var fixture = await GatewayFixture.StartAsync(
            configureServices: services => services.AddSingleton<ILoggerProvider>(logs));

        using var response = await GetAsync(fixture, "/health/live", "agentsplice:5280");

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

        var refusal = Assert.Single(logs.Records, record => record.EventId.Name == "NoAllowedHostMatched");

        Assert.Equal(LogLevel.Information, refusal.Level);
        Assert.Contains("'agentsplice:5280'", refusal.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_name_the_operator_allows_is_served_and_the_loopback_default_steps_aside()
    {
        // ASP.NET Core's own setting, for a deployment reached by another name: a Compose service, a
        // LAN address, a proxy. Naming hosts replaces the default rather than adding to it.
        await using var fixture = await GatewayFixture.StartAsync(settings => settings["AllowedHosts"] = "agentsplice");

        using var allowed = await GetAsync(fixture, "/health/live", "agentsplice:5280");
        using var other = await GetAsync(fixture, "/health/live", "attacker.example");

        Assert.True(allowed.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, other.StatusCode);
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("application/x-www-form-urlencoded")]
    [InlineData("multipart/form-data; boundary=x")]
    [InlineData(null)]
    public async Task A_completion_not_declared_as_json_is_refused_and_never_forwarded(string? mediaType)
    {
        // The three content types a page can post cross-site without a preflight, and none at all,
        // which a page can send too. Each used to run inference and add an exchange to the store.
        var sink = new CapturingExchangeSink();

        await using var fixture = await GatewayFixture.StartAsync(
            settings =>
            {
                settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
                settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
            },
            services => services.AddSingleton<IExchangeRecordSink>(sink));

        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"hi"}]}"""));

        if (mediaType is not null)
        {
            content.Headers.TryAddWithoutValidation("Content-Type", mediaType);
        }

        using var response = await fixture.Client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Contains("agentsplice_invalid_request", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        Assert.True(response.Headers.Contains("x-agentsplice-request-id"));
        Assert.Empty(fixture.Upstream.ReceivedRequests);

        // Not recorded either: a page that cannot run inference cannot fill the store instead. The
        // JSON request that follows is recorded, so a record of the refusal would be there first.
        using var json = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"hi"}]}"""));
        json.Headers.TryAddWithoutValidation("Content-Type", "application/json");

        using var served = await fixture.Client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), json);

        Assert.Equal(HttpStatusCode.OK, served.StatusCode);

        await sink.WaitForRecordAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(Assert.Single(sink.Records).Exchange);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("Application/JSON")]
    public async Task A_completion_declared_as_json_is_forwarded(string mediaType)
    {
        await using var fixture = await GatewayFixture.StartAsync(settings =>
        {
            settings["agentsplice:defaultRuntimeId"] = GatewayFixture.RuntimeId;
            settings[GatewayFixture.RuntimeKey(0, "discovery:enabled")] = "false";
        });

        fixture.Upstream.EnqueueFor("/v1/chat/completions", UpstreamResponseScripts.Json(Completion));

        using var content = new ByteArrayContent(Encoding.UTF8.GetBytes("""{"model":"m","messages":[{"role":"user","content":"hi"}]}"""));
        content.Headers.TryAddWithoutValidation("Content-Type", mediaType);

        using var response = await fixture.Client.PostAsync(new Uri("/v1/chat/completions", UriKind.Relative), content);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<HttpResponseMessage> GetAsync(GatewayFixture fixture, string path, string host)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(path, UriKind.Relative));
        request.Headers.Host = host;

        return await fixture.Client.SendAsync(request);
    }
}
