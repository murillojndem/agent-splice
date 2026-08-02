using System.Net;
using System.Text;
using System.Text.Json;
using AgentSplice.Api.Hosting;
using AgentSplice.Application.Configuration;
using AgentSplice.Protocols.OpenAI.Errors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentSplice.IntegrationTests.Administration;

/// <summary>
/// The endpoint filter's own decisions, including the ones a remote caller triggers
/// (FR-HEALTH-006, docs/SECURITY.md).
/// </summary>
/// <remarks>
/// Driven against the filter directly rather than through <c>WebApplicationFactory</c>, because that
/// host's in-process server presents no remote address at all — every request looks like loopback, so
/// the case this filter exists for is the one case it cannot exercise. A <see cref="DefaultHttpContext"/>
/// lets the remote address be stated, which is what the rule turns on.
/// </remarks>
public sealed class AdministrationFilterTests
{
    private const string TokenVariable = "AGENTSPLICE_TEST_FILTER_TOKEN";
    private const string Token = "SENTINEL-ADMIN-TOKEN-4f21ab";

    private static readonly IPAddress Remote = IPAddress.Parse("203.0.113.7");

    [Fact]
    public async Task A_remote_caller_without_a_token_is_refused()
    {
        using var scope = new TokenScope(Token);

        var outcome = await InvokeAsync(scope, Remote, authorization: null);

        Assert.False(outcome.Executed);
        Assert.Equal(401, outcome.StatusCode);
    }

    [Fact]
    public async Task A_remote_caller_with_the_wrong_token_is_refused()
    {
        using var scope = new TokenScope(Token);

        var outcome = await InvokeAsync(scope, Remote, "Bearer not-the-token-at-all-4f21ab");

        Assert.False(outcome.Executed);
        Assert.Equal(401, outcome.StatusCode);
    }

    [Fact]
    public async Task A_remote_caller_with_the_right_token_reaches_the_endpoint()
    {
        using var scope = new TokenScope(Token);

        var outcome = await InvokeAsync(scope, Remote, "Bearer " + Token);

        Assert.True(outcome.Executed);
    }

    [Fact]
    public async Task A_loopback_caller_needs_the_token_too_when_one_is_configured()
    {
        // The correction that matters. Behind a local reverse proxy — nginx, Caddy — Kestrel sees the
        // proxy's loopback address, so "arrived from 127.0.0.1" and "was made on this machine" are
        // the same observation and cannot be told apart. Trusting the first let a relayed external
        // request skip the token entirely.
        using var scope = new TokenScope(Token);

        var refused = await InvokeAsync(scope, IPAddress.Loopback, authorization: null);
        var allowed = await InvokeAsync(scope, IPAddress.Loopback, "Bearer " + Token);

        Assert.False(refused.Executed);
        Assert.Equal(401, refused.StatusCode);
        Assert.True(allowed.Executed);
    }

    [Fact]
    public async Task A_loopback_caller_is_served_when_no_token_is_configured()
    {
        // The supported unauthenticated deployment: a local process reached directly. Startup refuses
        // to combine this with a binding a network can reach.
        using var scope = new TokenScope(value: null);

        var outcome = await InvokeAsync(scope, IPAddress.Loopback, authorization: null);

        Assert.True(outcome.Executed);
    }

    [Fact]
    public async Task A_remote_caller_is_refused_when_no_token_is_configured()
    {
        using var scope = new TokenScope(value: null);

        var outcome = await InvokeAsync(scope, Remote, authorization: null);

        Assert.False(outcome.Executed);
        Assert.Equal(401, outcome.StatusCode);
    }

    [Fact]
    public async Task A_named_but_empty_variable_authorises_nobody()
    {
        // Neither "no token configured" — which would silently downgrade a deployment that asked for
        // authentication to loopback-only — nor a token, which would let an empty bearer in.
        using var scope = new TokenScope(string.Empty);

        var loopback = await InvokeAsync(scope, IPAddress.Loopback, "Bearer ");
        var remote = await InvokeAsync(scope, Remote, authorization: null);

        Assert.False(loopback.Executed);
        Assert.False(remote.Executed);
    }

    [Theory]
    [InlineData("Basic dXNlcjpwYXNz")]
    [InlineData("bearer " + Token)]
    [InlineData(Token)]
    [InlineData("Bearer")]
    [InlineData("")]
    public async Task A_malformed_authorization_scheme_is_refused(string authorization)
    {
        using var scope = new TokenScope(Token);

        var outcome = await InvokeAsync(scope, Remote, authorization);

        Assert.False(outcome.Executed);
        Assert.Equal(401, outcome.StatusCode);
    }

    [Fact]
    public async Task A_refusal_advertises_the_scheme_and_echoes_nothing_it_was_given()
    {
        const string Presented = "SENTINEL-PRESENTED-TOKEN-9c3";

        using var scope = new TokenScope(Token);

        var outcome = await InvokeAsync(scope, Remote, "Bearer " + Presented);

        // RFC 9110: a 401 has to say how to authenticate.
        Assert.Equal("Bearer", outcome.WwwAuthenticate);

        // The envelope names the mechanism and nothing about this deployment or this caller: not the
        // configured token, not the presented one, not the variable, not the address.
        Assert.DoesNotContain(Presented, outcome.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Token, outcome.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(TokenVariable, outcome.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(Remote.ToString(), outcome.Body, StringComparison.Ordinal);

        var error = JsonDocument.Parse(outcome.Body).RootElement.GetProperty("error");

        Assert.Equal("agentsplice_administration_unauthorized", error.GetProperty("code").GetString());
    }

    private static async Task<Outcome> InvokeAsync(TokenScope scope, IPAddress remote, string? authorization)
    {
        var options = new AgentSpliceOptions();
        options.Administration.ApiKeyEnvironmentVariable = scope.Variable;

        var filter = new AdministrationAuthorization(
            Options.Create(options),
            new OpenAiErrorEnvelopeWriter());

        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remote;
        context.Response.Body = new MemoryStream();

        if (authorization is not null)
        {
            context.Request.Headers.Authorization = authorization;
        }

        var executed = false;

        var result = await filter.InvokeAsync(
            EndpointFilterInvocationContext.Create(context),
            _ =>
            {
                executed = true;
                return ValueTask.FromResult<object?>(null);
            });

        // The filter returns the refusal rather than writing it, so the test executes it the way the
        // framework would.
        if (result is IResult refusal)
        {
            await refusal.ExecuteAsync(context);
        }

        context.Response.Body.Position = 0;

        return new Outcome(
            executed,
            context.Response.StatusCode,
            context.Response.Headers.WWWAuthenticate.ToString(),
            await new StreamReader(context.Response.Body, Encoding.UTF8).ReadToEndAsync());
    }

    private sealed record Outcome(bool Executed, int StatusCode, string WwwAuthenticate, string Body);

    /// <summary>Sets the token variable for one test and removes it afterwards.</summary>
    private sealed class TokenScope : IDisposable
    {
        internal TokenScope(string? value)
        {
            if (value is null)
            {
                Variable = null;
                return;
            }

            Variable = TokenVariable;
            Environment.SetEnvironmentVariable(TokenVariable, value);
        }

        internal string? Variable { get; }

        public void Dispose() => Environment.SetEnvironmentVariable(TokenVariable, null);
    }
}
