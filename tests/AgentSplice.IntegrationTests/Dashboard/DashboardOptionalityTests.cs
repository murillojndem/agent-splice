using System.Net;
using AgentSplice.Application.Diagnostics;
using AgentSplice.TestSupport;
using AgentSplice.TestSupport.Logging;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentSplice.IntegrationTests.Dashboard;

/// <summary>
/// The dashboard is optional, and where its build is looked for (FR-DASH-002, FR-DASH-007, ADR 0015).
/// </summary>
/// <remarks>
/// A gateway run from source has no build until someone runs <c>pnpm build</c>, and a deployment may
/// ship one it does not want served. Neither may cost the gateway anything: it starts, serves every
/// API, and says once what it did about the dashboard — or, when told not to serve it, says nothing.
///
/// Every other integration test boots with the shipped default and no build at that path, so the
/// whole suite also proves continuously that a missing build leaves the APIs alone.
/// </remarks>
public sealed class DashboardOptionalityTests
{
    [Fact]
    public async Task A_missing_build_leaves_the_gateway_serving_its_apis_and_nothing_at_the_mount_path()
    {
        using var host = new DashboardTestHost(settings => settings["agentsplice:dashboard:rootPath"] = MissingDirectory());

        using var shell = await host.GetAsync("/dashboard/");
        using var root = await host.GetAsync("/");
        using var liveness = await host.GetAsync("/health/live");
        using var system = await host.GetAsync("/api/v1/system");

        Assert.Equal(HttpStatusCode.NotFound, shell.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, liveness.StatusCode);
        Assert.Equal(HttpStatusCode.OK, system.StatusCode);
    }

    [Fact]
    public async Task A_missing_build_is_reported_once_as_a_warning_naming_where_the_gateway_looked()
    {
        // A relative path resolved against a directory nobody expected is the usual reason a build
        // "is not there", so the warning names the absolute path that was checked.
        var missing = MissingDirectory();
        var logs = new CapturingLoggerProvider();
        using var host = new DashboardTestHost(settings => settings["agentsplice:dashboard:rootPath"] = missing, logs);

        using var response = await host.GetAsync("/dashboard/");

        var warning = Assert.Single(logs.Records, record => record.EventId == GatewayEventIds.DashboardBuildMissing);

        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains(warning.AllText(), text => text.Contains(missing, StringComparison.Ordinal));
        Assert.DoesNotContain(logs.Records, record => record.EventId == GatewayEventIds.DashboardServed);
    }

    [Fact]
    public async Task A_directory_with_an_index_but_no_built_assets_is_not_mistaken_for_a_build()
    {
        // The dashboard's source directory has an index.html too. Served, it would be a page that
        // loads nothing beside the whole source tree.
        using var build = new TemporaryDashboardBuild(withAssets: false);
        var logs = new CapturingLoggerProvider();
        using var host = new DashboardTestHost(build.ApplyTo, logs);

        using var response = await host.GetAsync("/dashboard/");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(logs.Records, record => record.EventId == GatewayEventIds.DashboardBuildMissing);
    }

    [Fact]
    public async Task A_disabled_dashboard_is_not_served_even_when_a_build_exists_and_logs_nothing()
    {
        // The switch is for a deployment that ships the build, as the image does, and wants no
        // browser-facing surface. Turning it off is a choice, not a fault, so nothing is logged.
        using var build = new TemporaryDashboardBuild();
        var logs = new CapturingLoggerProvider();
        using var host = new DashboardTestHost(
            settings =>
            {
                build.ApplyTo(settings);
                settings["agentsplice:dashboard:enabled"] = "false";
            },
            logs);

        using var shell = await host.GetAsync("/dashboard/");
        using var script = await host.GetAsync(TemporaryDashboardBuild.ScriptPath);
        using var root = await host.GetAsync("/");

        Assert.Equal(HttpStatusCode.NotFound, shell.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, script.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        Assert.DoesNotContain(
            logs.Records,
            record => record.EventId == GatewayEventIds.DashboardServed
                || record.EventId == GatewayEventIds.DashboardBuildMissing);
    }

    [Fact]
    public async Task A_relative_root_path_is_resolved_against_the_content_root_not_the_working_directory()
    {
        // The shipped default is relative — "dashboard", which is /app/dashboard in the image because
        // /app is the content root there. The test host's content root is the Api project and its
        // working directory is the test output folder, so the two readings point at different places
        // and only one of them finds the build. It is created under the test output so that the
        // relative path stays on one drive.
        using var build = new TemporaryDashboardBuild(parent: AppContext.BaseDirectory);

        var contentRoot = RepositoryPaths.Resolve("src", "AgentSplice.Api");
        var relative = Path.GetRelativePath(contentRoot, build.Root);

        Assert.False(Path.IsPathRooted(relative));
        Assert.False(
            File.Exists(Path.Combine(Path.GetFullPath(relative, Environment.CurrentDirectory), "index.html")),
            "The relative path must not also resolve from the working directory, or this test proves nothing.");

        using var host = new DashboardTestHost(settings => settings["agentsplice:dashboard:rootPath"] = relative);

        using var response = await host.GetAsync("/dashboard/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(TemporaryDashboardBuild.ShellMarker, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_absolute_root_path_is_used_as_given()
    {
        using var build = new TemporaryDashboardBuild();

        Assert.True(Path.IsPathFullyQualified(build.Root));

        using var host = new DashboardTestHost(build.ApplyTo);

        using var response = await host.GetAsync("/dashboard/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private static string MissingDirectory() =>
        Path.Combine(Path.GetTempPath(), "agentsplice-tests", "no-dashboard-" + Guid.NewGuid().ToString("N"));
}
