using System.Net;
using System.Net.Http.Headers;
using AgentSplice.Application.Diagnostics;
using AgentSplice.TestSupport.Logging;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Dashboard;

/// <summary>
/// The gateway serves the dashboard build at <c>/dashboard/</c> on its own origin (FR-DASH-002,
/// FR-DASH-003, FR-DASH-007, FR-DASH-008, ADR 0015).
/// </summary>
/// <remarks>
/// What an alpha installation needs is a page that opens, deep links that survive a reload, and
/// hashed files cached for as long as they cannot change. What everyone else needs is for none of
/// that to change how any other route answers, and for the page to be unable to run script it did
/// not ship — it holds the operator's token, on the same origin as the evidence that token reads.
/// </remarks>
public sealed class DashboardServingTests
{
    private const string TokenVariable = "AGENTSPLICE_TEST_DASHBOARD_ADMIN_TOKEN";

    [Fact]
    public async Task The_shell_is_served_at_the_mount_path()
    {
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.GetAsync("/dashboard/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(TemporaryDashboardBuild.ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_mount_path_without_its_trailing_slash_redirects_to_the_shell_keeping_the_query()
    {
        // The client router's basename is /dashboard/, and at /dashboard it renders nothing. The
        // redirect makes the URL a person types land somewhere.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.GetAsync("/dashboard?status=failed");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard/?status=failed", response.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("/dashboard/exchanges")]
    [InlineData("/dashboard/exchanges/9f0e8a62-6c1d-4f3e-9b7a-2d5c4e1f0a3b")]
    [InlineData("/dashboard/runtimes")]
    public async Task A_deep_link_to_a_client_route_is_answered_with_the_shell(string path)
    {
        // Reloading a page the client router navigated to asks the server for that path. Anything but
        // the shell there is a broken bookmark.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains(TemporaryDashboardBuild.ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_client_route_whose_last_segment_contains_a_dot_is_still_answered_with_the_shell()
    {
        // Model and runtime identifiers carry dots. A "looks like a file" rule would turn a future
        // route naming one into a 404 on reload.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.GetAsync("/dashboard/models/qwen3.6-27b-mtp");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TemporaryDashboardBuild.ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_hashed_asset_is_served_with_its_own_type_and_cached_as_immutable()
    {
        // Vite puts a content hash in every file under assets/, so a changed file is a different URL
        // and the old one can be cached for as long as a browser will keep it.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var script = await host.GetAsync(TemporaryDashboardBuild.ScriptPath);
        using var stylesheet = await host.GetAsync(TemporaryDashboardBuild.StylesheetPath);

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Equal(TemporaryDashboardBuild.ScriptBody, await script.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, stylesheet.StatusCode);
        Assert.Equal("text/css", stylesheet.Content.Headers.ContentType?.MediaType);

        foreach (var response in new[] { script, stylesheet })
        {
            var caching = response.Headers.CacheControl;

            Assert.NotNull(caching);
            Assert.True(caching.Public);
            Assert.Equal(TimeSpan.FromDays(365), caching.MaxAge);
            Assert.Contains(caching.Extensions, extension => extension.Name == "immutable");
        }
    }

    [Theory]
    [InlineData("/dashboard/assets/missing-0d1e2f.js")]
    [InlineData("/dashboard/assets/")]
    [InlineData("/dashboard/assets/without-an-extension")]
    public async Task A_missing_asset_is_a_404_and_never_the_shell(string path)
    {
        // A browser that asked for a script and received HTML refuses it under nosniff and reports a
        // MIME error that points nowhere near the cause: a build that does not match its shell.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.DoesNotContain(TemporaryDashboardBuild.ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_shell_is_revalidated_on_every_load_and_answers_a_conditional_request_with_304()
    {
        // The shell names the hashed files, so a cached shell after an upgrade would load the previous
        // build. Revalidating costs a 304 when nothing changed.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var first = await host.GetAsync("/dashboard/exchanges");
        using var byName = await host.GetAsync("/dashboard/index.html");

        foreach (var response in new[] { first, byName })
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl?.NoCache);
            Assert.Null(response.Headers.CacheControl?.MaxAge);
        }

        var tag = first.Headers.ETag;
        Assert.NotNull(tag);

        using var conditional = new HttpRequestMessage(HttpMethod.Get, new Uri("/dashboard/runtimes", UriKind.Relative));
        conditional.Headers.IfNoneMatch.Add(tag);

        using var revalidated = await host.Client.SendAsync(conditional);

        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
    }

    [Fact]
    public async Task Every_dashboard_response_carries_the_content_security_policy_and_the_framing_and_sniffing_guards()
    {
        // The policy matters on the shell, but it is set on everything under the mount — refusals and
        // redirects included — so no response there is the one that was forgotten.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        string[] expectedDirectives =
        [
            "default-src 'none'",
            "script-src 'self'",
            "style-src 'self'",
            "img-src 'self' data:",
            "font-src 'self'",
            "connect-src 'self'",
            "base-uri 'none'",
            "form-action 'none'",
            "frame-ancestors 'none'",
        ];

        var responses = new List<HttpResponseMessage>
        {
            await host.GetAsync("/dashboard/"),
            await host.GetAsync(TemporaryDashboardBuild.ScriptPath),
            await host.GetAsync("/dashboard/assets/missing-0d1e2f.js"),
            await host.SendAsync(HttpMethod.Post, "/dashboard/"),
            await host.GetAsync("/dashboard"),
        };

        try
        {
            foreach (var response in responses)
            {
                var policy = DashboardTestHost.Header(response, "Content-Security-Policy");

                Assert.NotNull(policy);

                var directives = policy
                    .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Order(StringComparer.Ordinal);

                Assert.Equal(expectedDirectives.Order(StringComparer.Ordinal), directives);
                Assert.DoesNotContain("unsafe-inline", policy, StringComparison.Ordinal);
                Assert.DoesNotContain("unsafe-eval", policy, StringComparison.Ordinal);

                Assert.Equal("nosniff", DashboardTestHost.Header(response, "X-Content-Type-Options"));
                Assert.Equal("DENY", DashboardTestHost.Header(response, "X-Frame-Options"));
                Assert.Equal("no-referrer", DashboardTestHost.Header(response, "Referrer-Policy"));
            }
        }
        finally
        {
            responses.ForEach(response => response.Dispose());
        }
    }

    [Fact]
    public async Task A_head_request_is_answered_with_headers_and_no_body()
    {
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var shell = await host.SendAsync(HttpMethod.Head, "/dashboard/runtimes");
        using var script = await host.SendAsync(HttpMethod.Head, TemporaryDashboardBuild.ScriptPath);

        Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
        Assert.Equal("text/html", shell.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await shell.Content.ReadAsByteArrayAsync());

        Assert.Equal(HttpStatusCode.OK, script.StatusCode);
        Assert.Equal("text/javascript", script.Content.Headers.ContentType?.MediaType);
        Assert.Empty(await script.Content.ReadAsByteArrayAsync());
    }

    [Theory]
    [InlineData("POST", "/dashboard/")]
    [InlineData("PUT", "/dashboard")]
    [InlineData("DELETE", TemporaryDashboardBuild.ScriptPath)]
    public async Task Methods_other_than_get_and_head_are_refused_with_405(string method, string path)
    {
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.SendAsync(new HttpMethod(method), path);

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.StatusCode);
        Assert.Equal("GET, HEAD", DashboardTestHost.Header(response, "Allow"));
    }

    [Fact]
    public async Task Nothing_outside_the_build_directory_and_no_hidden_file_is_ever_served()
    {
        // The traversal attempts go through the in-process server directly, because HttpClient would
        // normalise the dot segments away before sending and the gateway would never see them. A path
        // that escapes the build is answered with the shell or a 404 — what matters is that the
        // sentinel beside the build, and the ones in its hidden files, never come back.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        string[] escapes =
        [
            "/dashboard/../outside.txt",
            "/dashboard/assets/../../outside.txt",
            "/dashboard/%2e%2e/outside.txt",
            "/dashboard/..%2foutside.txt",
        ];

        foreach (var path in escapes)
        {
            var context = await host.Server.SendAsync(http =>
            {
                http.Request.Method = HttpMethods.Get;
                http.Request.Path = new PathString(path);
            });

            using var reader = new StreamReader(context.Response.Body);
            var body = await reader.ReadToEndAsync();

            Assert.DoesNotContain(TemporaryDashboardBuild.OutsideSentinel, body, StringComparison.Ordinal);
        }

        foreach (var path in new[] { "/dashboard/.secret", "/dashboard/assets/.hidden.js" })
        {
            using var response = await host.GetAsync(path);

            Assert.DoesNotContain(TemporaryDashboardBuild.HiddenSentinel, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("HEAD")]
    public async Task The_root_of_the_gateway_redirects_a_browser_to_the_dashboard(string method)
    {
        // Someone pointing a browser at the address the gateway prints lands on the page they came for.
        using var build = new TemporaryDashboardBuild();
        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.SendAsync(new HttpMethod(method), "/");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/dashboard/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task The_shell_needs_no_credential_while_the_administrative_api_still_does()
    {
        // The shell is the same public code for everyone and holds no evidence; the evidence stays
        // behind /api/v1, where a configured token is required from every caller (FR-HEALTH-006).
        const string Token = "SENTINEL-ADMIN-TOKEN-dashboard";

        Environment.SetEnvironmentVariable(TokenVariable, Token);

        try
        {
            using var build = new TemporaryDashboardBuild();
            using var host = new DashboardTestHost(settings =>
            {
                build.ApplyTo(settings);
                settings["agentsplice:administration:apiKeyEnvironmentVariable"] = TokenVariable;
            });

            using var shell = await host.GetAsync("/dashboard/");
            using var script = await host.GetAsync(TemporaryDashboardBuild.ScriptPath);
            using var refused = await host.GetAsync("/api/v1/system");

            using var authorised = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/system", UriKind.Relative));
            authorised.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            using var allowed = await host.Client.SendAsync(authorised);

            Assert.Equal(HttpStatusCode.OK, shell.StatusCode);
            Assert.Equal(HttpStatusCode.OK, script.StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, refused.StatusCode);
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        }
        finally
        {
            Environment.SetEnvironmentVariable(TokenVariable, null);
        }
    }

    [Fact]
    public async Task Serving_the_dashboard_leaves_every_api_answering_as_before()
    {
        // The reason the dashboard lives in a branch under /dashboard rather than behind a fallback at
        // the root: a root fallback turns the first two into 200 pages and the third into HTML that the
        // dashboard's own client would read as an empty result. Each request is compared with the same
        // request to a gateway that serves no dashboard.
        (HttpMethod Method, string Path)[] requests =
        [
            (HttpMethod.Get, "/v1/chat/completions"),
            (HttpMethod.Head, "/health/live"),
            (HttpMethod.Get, "/api/v1/no-such-route"),
            (HttpMethod.Get, "/health/no-such-probe"),
            (HttpMethod.Get, "/health/live"),
            (HttpMethod.Get, "/dashboards"),
        ];

        using var build = new TemporaryDashboardBuild();
        using var served = new DashboardTestHost(build.ApplyTo);
        using var absent = new DashboardTestHost(settings =>
        {
            build.ApplyTo(settings);
            settings["agentsplice:dashboard:enabled"] = "false";
        });

        foreach (var (method, path) in requests)
        {
            using var withDashboard = await served.SendAsync(method, path);
            using var withoutDashboard = await absent.SendAsync(method, path);

            Assert.Equal(withoutDashboard.StatusCode, withDashboard.StatusCode);
            Assert.Equal(
                withoutDashboard.Content.Headers.ContentType?.MediaType,
                withDashboard.Content.Headers.ContentType?.MediaType);
            Assert.Null(DashboardTestHost.Header(withDashboard, "Content-Security-Policy"));
        }

        using var methodMismatch = await served.GetAsync("/v1/chat/completions");
        using var unknownApi = await served.GetAsync("/api/v1/no-such-route");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, methodMismatch.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, unknownApi.StatusCode);
        Assert.NotEqual("text/html", unknownApi.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Serving_the_dashboard_is_announced_once_with_the_directory_it_serves()
    {
        // The startup log says where the page an operator is looking at came from, which is the first
        // question when it is not the build they expected.
        using var build = new TemporaryDashboardBuild();
        var logs = new CapturingLoggerProvider();
        using var host = new DashboardTestHost(build.ApplyTo, logs);

        using var response = await host.GetAsync("/dashboard/");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var announcement = Assert.Single(logs.Records, record => record.EventId == GatewayEventIds.DashboardServed);

        Assert.Equal(LogLevel.Information, announcement.Level);
        Assert.Contains(announcement.AllText(), text => text.Contains(build.Root, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Records, record => record.EventId == GatewayEventIds.DashboardBuildMissing);
    }
}
