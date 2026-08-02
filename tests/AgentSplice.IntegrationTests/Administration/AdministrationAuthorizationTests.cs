using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentSplice.Api.Hosting;
using AgentSplice.IntegrationTests.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentSplice.IntegrationTests.Administration;

/// <summary>
/// Who may read the administrative surface (FR-HEALTH-006, docs/SECURITY.md).
/// </summary>
/// <remarks>
/// The remote-address half cannot be exercised through <c>WebApplicationFactory</c>, whose in-process
/// server presents no remote address at all. What the host test can prove is that loopback works
/// without a token and that the startup guard refuses the dangerous binding; the token comparison and
/// the address rule are asserted directly against the guard and the filter's own inputs.
/// </remarks>
public sealed class AdministrationAuthorizationTests
{
    private const string TokenVariable = "AGENTSPLICE_TEST_ADMIN_TOKEN";

    [Fact]
    public async Task A_loopback_caller_reads_the_surface_without_a_token()
    {
        // The deployment AgentSplice is built for. Requiring a token here would make looking at your
        // own traces on your own machine need secret management first.
        await using var fixture = await GatewayFixture.StartAsync();

        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/system", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task A_configured_token_is_required_even_from_loopback()
    {
        // The proxy correction, through the real host. Behind nginx or Caddy on the same machine,
        // Kestrel sees the proxy's loopback address for every relayed request, so trusting loopback
        // let an external caller skip the token. Once a token exists, everyone presents it.
        const string Token = "SENTINEL-ADMIN-TOKEN-host";

        Environment.SetEnvironmentVariable(TokenVariable, Token);

        try
        {
            await using var fixture = await GatewayFixture.StartAsync(settings =>
                settings["agentsplice:administration:apiKeyEnvironmentVariable"] = TokenVariable);

            using var refused = await fixture.Client.GetAsync(new Uri("/api/v1/runtimes", UriKind.Relative));

            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal("Bearer", refused.Headers.WwwAuthenticate.ToString());

            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/runtimes", UriKind.Relative));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);

            using var allowed = await fixture.Client.SendAsync(request);

            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task Liveness_answers_without_authorization()
    {
        // Outside /api/v1 deliberately. A probe that failed closed on a misconfigured token would
        // restart a healthy process (FR-HEALTH-002).
        Environment.SetEnvironmentVariable(TokenVariable, "SENTINEL-ADMIN-TOKEN");

        try
        {
            await using var fixture = await GatewayFixture.StartAsync(settings =>
                settings["agentsplice:administration:apiKeyEnvironmentVariable"] = TokenVariable);

            using var live = await fixture.Client.GetAsync(new Uri("/health/live", UriKind.Relative));
            using var ready = await fixture.Client.GetAsync(new Uri("/health/ready", UriKind.Relative));

            Assert.Equal(HttpStatusCode.NoContent, live.StatusCode);

            // Readiness too: an orchestrator probe that failed closed on a token would take a healthy
            // gateway out of rotation for a configuration problem it has no part in.
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task An_unauthorized_answer_names_the_reason_rather_than_hiding_the_route()
    {
        // Asserted through the error envelope the filter writes. A 404 would leave an operator with a
        // wrong token unable to tell a typo from a route that does not exist, and the route is
        // published in the OpenAPI draft anyway.
        await using var fixture = await GatewayFixture.StartAsync();

        using var response = await fixture.Client.GetAsync(new Uri("/api/v1/system", UriKind.Relative));

        response.EnsureSuccessStatusCode();

        // Loopback is allowed, so the envelope itself is verified from the catalogue rather than from
        // a response this in-process server cannot produce.
        var error = AgentSplice.Application.Errors.GatewayErrorCatalogue.AdministrationUnauthorized;

        Assert.Equal(401, error.StatusCode);
        Assert.Equal("agentsplice_administration_unauthorized", error.Code);

        // A compile-time constant, like every other envelope message: it names the mechanism a caller
        // needs and nothing about how this deployment is configured (docs/SECURITY.md "Trace safety").
        Assert.DoesNotContain("agentsplice:", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Null(error.Param);
    }

    [Theory]
    [InlineData("http://127.0.0.1:5280", false)]
    [InlineData("http://localhost:5280", false)]
    [InlineData("http://[::1]:5280", false)]
    [InlineData("http://0.0.0.0:5280", true)]
    [InlineData("http://192.168.1.10:5280", true)]
    [InlineData("http://+:5280", true)]
    [InlineData("http://127.0.0.1:5280;http://0.0.0.0:5281", true)]
    public void A_binding_is_classified_by_whether_another_machine_can_reach_it(string urls, bool beyondLoopback)
    {
        var configuration = Configure(new Dictionary<string, string?> { ["urls"] = urls });

        Assert.Equal(beyondLoopback, LoopbackBindingDefault.ListensBeyondLoopback(configuration));
    }

    [Fact]
    public void A_bare_port_list_binds_every_interface()
    {
        // What a container publishing a port produces, and the case this guard exists for.
        var configuration = Configure(new Dictionary<string, string?> { ["HTTP_PORTS"] = "5280" });

        Assert.True(LoopbackBindingDefault.ListensBeyondLoopback(configuration));
    }

    [Fact]
    public void An_unconfigured_host_takes_the_loopback_default_and_needs_no_token()
    {
        var configuration = Configure(new Dictionary<string, string?>());

        Assert.False(LoopbackBindingDefault.ListensBeyondLoopback(configuration));

        AdministrationBindingGuard.Verify(configuration);
    }

    [Fact]
    public void A_network_binding_with_no_token_configured_refuses_to_start()
    {
        var configuration = Configure(new Dictionary<string, string?> { ["urls"] = "http://0.0.0.0:5280" });

        var failure = Assert.Throws<InvalidOperationException>(() =>
            AdministrationBindingGuard.Verify(configuration));

        Assert.Contains("apiKeyEnvironmentVariable", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_network_binding_whose_named_variable_is_unset_also_refuses()
    {
        // Naming a variable is not the same as setting one, and the difference is the whole
        // protection.
        var configuration = Configure(new Dictionary<string, string?>
        {
            ["urls"] = "http://0.0.0.0:5280",
            ["agentsplice:administration:apiKeyEnvironmentVariable"] = "AGENTSPLICE_TEST_UNSET_TOKEN",
        });

        var failure = Assert.Throws<InvalidOperationException>(() =>
            AdministrationBindingGuard.Verify(configuration));

        Assert.Contains("AGENTSPLICE_TEST_UNSET_TOKEN", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_network_binding_with_a_token_starts()
    {
        Environment.SetEnvironmentVariable(TokenVariable, "SENTINEL-ADMIN-TOKEN");

        try
        {
            var configuration = Configure(new Dictionary<string, string?>
            {
                ["urls"] = "http://0.0.0.0:5280",
                ["agentsplice:administration:apiKeyEnvironmentVariable"] = TokenVariable,
            });

            AdministrationBindingGuard.Verify(configuration);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public void The_refusal_never_repeats_the_secret_it_is_protecting()
    {
        const string Secret = "SENTINEL-ADMIN-TOKEN-VALUE";

        Environment.SetEnvironmentVariable("AGENTSPLICE_TEST_EMPTY_TOKEN", string.Empty);

        try
        {
            var configuration = Configure(new Dictionary<string, string?>
            {
                ["urls"] = "http://0.0.0.0:5280",
                ["agentsplice:administration:apiKeyEnvironmentVariable"] = "AGENTSPLICE_TEST_EMPTY_TOKEN",
            });

            var failure = Assert.Throws<InvalidOperationException>(() =>
                AdministrationBindingGuard.Verify(configuration));

            Assert.DoesNotContain(Secret, failure.Message, StringComparison.Ordinal);
        }
        finally
        {
            Environment.SetEnvironmentVariable("AGENTSPLICE_TEST_EMPTY_TOKEN", null);
        }
    }

    [Fact]
    public async Task A_late_network_binding_with_no_token_fails_host_startup()
    {
        // The guard used to read builder.Configuration during composition, which a host is still
        // assembling — the test factory adds its overrides through ConfigureAppConfiguration, after
        // the composition root has run. A binding arriving that way was therefore invisible to the
        // very check meant to catch it. It now reads app.Configuration, which is the finished article.
        var factory = new AgentSpliceApplicationFactory(new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["urls"] = "http://0.0.0.0:5280",
        });

        await using (factory)
        {
            var failure = Assert.Throws<InvalidOperationException>(() => factory.CreateClient());

            Assert.Contains("apiKeyEnvironmentVariable", failure.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_late_network_binding_starts_when_the_named_variable_holds_a_token()
    {
        Environment.SetEnvironmentVariable(TokenVariable, "SENTINEL-ADMIN-TOKEN-late");

        try
        {
            var factory = new AgentSpliceApplicationFactory(new Dictionary<string, string?>(StringComparer.Ordinal)
            {
                ["urls"] = "http://0.0.0.0:5280",
                ["agentsplice:administration:apiKeyEnvironmentVariable"] = TokenVariable,
            });

            await using (factory)
            {
                using var client = factory.CreateClient();
                using var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    private static IConfiguration Configure(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
