using System.Text.RegularExpressions;
using AgentSplice.Api.Hosting;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.IntegrationTests.Dashboard;

/// <summary>
/// The dashboard build and the gateway agree on where the dashboard lives (FR-DASH-007, ADR 0015).
/// </summary>
/// <remarks>
/// Two build systems share two facts and neither can see the other's. Vite's <c>base</c> is written
/// into every asset URL in the shell and into the client router's basename; the gateway mounts the
/// build at a path of its own. If they drift, the shell loads and every script it names is a 404 — a
/// blank page, and nothing in either build fails. The same holds for the directory Vite writes hashed
/// files to and the one the gateway caches as immutable.
///
/// Here rather than among the contract tests because the gateway's side of each fact is an internal
/// constant of the Api assembly, which this project can see.
/// </remarks>
public sealed partial class DashboardBuildContractTests
{
    [Fact]
    public void The_dashboard_build_and_the_gateway_agree_on_the_mount_path()
    {
        var match = BaseSetting().Match(ViteConfiguration());

        Assert.True(match.Success, "vite.config.ts must set base explicitly.");
        Assert.Equal(DashboardHosting.ShellUrl, match.Groups["value"].Value);
        Assert.Equal(DashboardHosting.MountPath.Value + "/", match.Groups["value"].Value);
    }

    [Fact]
    public void The_build_writes_hashed_files_where_the_gateway_caches_them_as_immutable()
    {
        var match = AssetsDirectorySetting().Match(ViteConfiguration());

        Assert.True(match.Success, "vite.config.ts must set build.assetsDir explicitly.");
        Assert.Equal(DashboardBuild.AssetsDirectoryName, match.Groups["value"].Value);
    }

    private static string ViteConfiguration() =>
        RepositoryPaths.ReadText("web", "AgentSplice.Dashboard", "vite.config.ts");

    [GeneratedRegex(@"^\s*base:\s*'(?<value>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex BaseSetting();

    [GeneratedRegex(@"^\s*assetsDir:\s*'(?<value>[^']*)'", RegexOptions.Multiline)]
    private static partial Regex AssetsDirectorySetting();
}
