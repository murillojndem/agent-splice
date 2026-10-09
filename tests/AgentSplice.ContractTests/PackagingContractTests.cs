using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.ContractTests;

/// <summary>
/// The container image and the settings it ships agree on where the dashboard is (FR-DASH-007,
/// ADR 0015).
/// </summary>
/// <remarks>
/// Neither failure here breaks a build or a test that boots the host. A Dockerfile that copies the
/// dashboard somewhere the shipped <c>rootPath</c> does not name produces an image that starts, serves
/// every API, and logs that no dashboard was found — the alpha's one visible feature missing, with
/// nothing red anywhere. And a <c>.dockerignore</c> pattern anchored at the root rather than at every
/// depth is invisible in CI, whose fresh checkout has no <c>node_modules</c> to leak: it fails only on
/// a developer's machine, as a native-binary error inside the image build.
/// </remarks>
public sealed partial class PackagingContractTests
{
    [Fact]
    public void The_image_puts_the_dashboard_where_the_shipped_settings_look_for_it()
    {
        using var settings = JsonDocument.Parse(RepositoryPaths.ReadText("src", "AgentSplice.Api", "appsettings.json"));

        var rootPath = settings.RootElement
            .GetProperty("agentsplice")
            .GetProperty("dashboard")
            .GetProperty("rootPath")
            .GetString();

        Assert.False(string.IsNullOrWhiteSpace(rootPath));
        Assert.False(Path.IsPathRooted(rootPath), "The shipped rootPath is resolved against the content root, /app in the image.");

        var dockerfile = RepositoryPaths.ReadText("Dockerfile");
        var stages = dockerfile.Split("\nFROM ", StringSplitOptions.None);

        var dashboardStage = Assert.Single(stages, stage => stage.Contains(" AS dashboard", StringComparison.Ordinal));
        Assert.Contains("WORKDIR /dashboard", dashboardStage, StringComparison.Ordinal);
        Assert.Contains("RUN pnpm build", dashboardStage, StringComparison.Ordinal);

        var runtimeStage = stages[^1];
        Assert.Contains("WORKDIR /app", runtimeStage, StringComparison.Ordinal);

        var copy = DashboardCopy().Match(runtimeStage);

        Assert.True(copy.Success, "The runtime stage must copy the dashboard build out of the dashboard stage.");
        Assert.Equal("/dashboard/dist", copy.Groups["source"].Value);
        Assert.Equal(rootPath, copy.Groups["target"].Value);
    }

    [Fact]
    public void The_image_context_excludes_dependencies_build_output_and_stores_at_any_depth()
    {
        var patterns = RepositoryPaths.ReadText(".dockerignore")
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0 && !line.StartsWith('#'))
            .ToHashSet(StringComparer.Ordinal);

        string[] required = ["**/node_modules/", "**/dist/", "**/*.db", "**/*.db-shm", "**/*.db-wal"];

        foreach (var pattern in required)
        {
            Assert.Contains(pattern, patterns);
        }

        // Root-level on purpose: the dashboard's own tests/ directory is type-checked by its build.
        Assert.Contains("tests/", patterns);
        Assert.DoesNotContain("**/tests/", patterns);
    }

    [GeneratedRegex(@"^COPY --from=dashboard (?<source>\S+) \./(?<target>[^/\s]+)/?\s*$", RegexOptions.Multiline)]
    private static partial Regex DashboardCopy();
}
