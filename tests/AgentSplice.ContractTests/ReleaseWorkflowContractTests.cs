using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// A tag becomes a reviewed draft, built by the full CI suite, published by the one job that may write
/// (ADR 0018).
/// </summary>
/// <remarks>
/// None of this runs until someone pushes a tag, and every way it can go wrong does so in public: a
/// release that skips the draft is published the moment it is built, a write token in a job that
/// installs packages hands repository write access to every dependency's install script, and an
/// artifact name that drifted publishes nothing, or something CI never checked. So the workflow file is
/// read and held to those properties on every build instead.
/// </remarks>
public sealed partial class ReleaseWorkflowContractTests
{
    private const string PackageArtifact = "agentsplice-win-x64";

    [Fact]
    public void A_release_is_only_ever_created_as_a_draft_pre_release()
    {
        var command = Assert.Single(
            Steps(Jobs(Release())).Select(Run),
            run => run.Contains("gh release create", StringComparison.Ordinal));

        Assert.Contains("--draft", command, StringComparison.Ordinal);
        Assert.Contains("--prerelease", command, StringComparison.Ordinal);
        Assert.Contains("--verify-tag", command, StringComparison.Ordinal);
    }

    [Fact]
    public void Only_the_publish_job_can_write_and_it_installs_nothing()
    {
        var release = Release();

        Assert.Equal("read", Scalar(Mapping(release, "permissions"), "contents"));
        Assert.Single(Mapping(release, "permissions").Children);

        foreach (var (name, job) in Jobs(release))
        {
            var permissions = job.Children.TryGetValue(new YamlScalarNode("permissions"), out var node)
                ? Assert.IsType<YamlMappingNode>(node)
                : null;

            var writes = permissions?.Children.Values.OfType<YamlScalarNode>().Any(value => value.Value == "write") ?? false;

            if (name != "publish")
            {
                Assert.False(writes, FormattableString.Invariant($"The '{name}' job must not be granted write access."));
                continue;
            }

            Assert.True(writes, "The publish job needs contents: write to create the release.");

            // What holds the write token must run nothing fetched from a registry: no checkout to build
            // from, and no restore or install whose scripts would run with the token in reach.
            foreach (var step in Steps(job))
            {
                var uses = Scalar(step, "uses") ?? string.Empty;
                var run = Run(step);

                Assert.False(uses.StartsWith("actions/checkout", StringComparison.Ordinal), "The publish job must not check out the repository.");
                Assert.DoesNotContain("pnpm install", run, StringComparison.Ordinal);
                Assert.DoesNotContain("dotnet restore", run, StringComparison.Ordinal);
                Assert.DoesNotContain("dotnet build", run, StringComparison.Ordinal);
                Assert.DoesNotContain("dotnet publish", run, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void The_release_publishes_the_package_ci_builds_and_checks()
    {
        var ci = Workflow("ci.yml");

        Assert.True(
            Mapping(ci, "on").Children.ContainsKey(new YamlScalarNode("workflow_call")),
            "ci.yml must be callable, so the release runs the whole suite against the tag.");

        var package = Jobs(ci).Single(job => job.Name == "package-windows").Job;
        var upload = Assert.Single(Steps(package), step => (Scalar(step, "uses") ?? string.Empty).StartsWith("actions/upload-artifact", StringComparison.Ordinal));
        Assert.Equal(PackageArtifact, Scalar(Mapping(upload, "with"), "name"));

        var jobs = Jobs(Release()).ToDictionary(job => job.Name, job => job.Job, StringComparer.Ordinal);

        Assert.Equal("./.github/workflows/ci.yml", Scalar(jobs["ci"], "uses"));
        Assert.Equal("ci", Scalar(jobs["publish"], "needs"));
        Assert.Equal("version", Scalar(jobs["ci"], "needs"));

        var download = Assert.Single(Steps(jobs["publish"]), step => (Scalar(step, "uses") ?? string.Empty).StartsWith("actions/download-artifact", StringComparison.Ordinal));
        Assert.Equal(PackageArtifact, Scalar(Mapping(download, "with"), "name"));
    }

    [Fact]
    public void Only_a_version_tag_starts_a_release_and_it_must_name_the_builds_version()
    {
        var release = Release();

        var tags = Assert.IsType<YamlSequenceNode>(Mapping(Mapping(release, "on"), "push").Children[new YamlScalarNode("tags")]);
        Assert.Equal(["v*"], tags.Children.OfType<YamlScalarNode>().Select(tag => tag.Value));
        Assert.Single(Mapping(release, "on").Children);

        var version = Jobs(release).Single(job => job.Name == "version").Job;
        var check = string.Join('\n', Steps(version).Select(Run));

        Assert.Contains("-getProperty:Version", check, StringComparison.Ordinal);
        Assert.Contains("GITHUB_REF_NAME", check, StringComparison.Ordinal);
    }

    [Fact]
    public void The_dashboard_and_the_gateway_carry_one_version()
    {
        // The tag is checked against the gateway's version; the dashboard ships in the same package
        // and must not claim another.
        var props = RepositoryPaths.ReadText("Directory.Build.props");
        var prefix = VersionPrefix().Match(props).Groups["value"].Value;
        var suffix = VersionSuffix().Match(props).Groups["value"].Value;
        var gateway = suffix.Length == 0 ? prefix : prefix + "-" + suffix;

        using var package = JsonDocument.Parse(RepositoryPaths.ReadText("web", "AgentSplice.Dashboard", "package.json"));

        Assert.False(string.IsNullOrEmpty(prefix));
        Assert.Equal(gateway, package.RootElement.GetProperty("version").GetString());
    }

    private static YamlMappingNode Release() => Workflow("release.yml");

    private static YamlMappingNode Workflow(string fileName)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText(".github", "workflows", fileName)));

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static IEnumerable<(string Name, YamlMappingNode Job)> Jobs(YamlMappingNode workflow) =>
        Mapping(workflow, "jobs").Children.Select(entry =>
            (((YamlScalarNode)entry.Key).Value!, Assert.IsType<YamlMappingNode>(entry.Value)));

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job) =>
        job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps)
            ? Assert.IsType<YamlSequenceNode>(steps).Children.Cast<YamlMappingNode>()
            : [];

    private static IEnumerable<YamlMappingNode> Steps(IEnumerable<(string Name, YamlMappingNode Job)> jobs) =>
        jobs.SelectMany(job => Steps(job.Job));

    private static string Run(YamlMappingNode step) => Scalar(step, "run") ?? string.Empty;

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    [GeneratedRegex(@"<VersionPrefix>(?<value>[^<]*)</VersionPrefix>")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"<VersionSuffix>(?<value>[^<]*)</VersionSuffix>")]
    private static partial Regex VersionSuffix();
}
