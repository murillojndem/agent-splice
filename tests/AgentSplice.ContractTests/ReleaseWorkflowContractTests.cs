using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// A tag becomes a reviewed draft, built and assembled by the full CI suite, published by the one job
/// that may write (ADR 0018, ADR 0021).
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
    private const string ReleaseArtifact = "agentsplice-release";

    /// <summary>What a job that only handles artifacts never runs.</summary>
    private static readonly string[] InstallCommands = ["pnpm", "npm ", "npx ", "dotnet ", "apt-get", "pip "];

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
    public void Every_platform_the_package_script_builds_is_built_smoke_tested_and_uploaded_by_ci()
    {
        var platforms = PackagePlatforms();
        var jobs = Jobs(Workflow("ci.yml")).ToArray();

        Assert.Contains("linux-x64", platforms);
        Assert.Contains("win-x64", platforms);

        foreach (var platform in platforms)
        {
            var job = Assert.Single(jobs, entry => Steps(entry.Job).Any(step =>
                Run(step).Contains("package.ps1 -RuntimeIdentifier " + platform + " ", StringComparison.Ordinal))).Job;

            Assert.Single(Steps(job), step => Run(step).Contains("smoke-package.ps1", StringComparison.Ordinal));

            var upload = Assert.Single(Steps(job), step => Uses(step).StartsWith("actions/upload-artifact", StringComparison.Ordinal));
            Assert.Equal("agentsplice-" + platform, Scalar(Mapping(upload, "with"), "name"));

            // The job reports the ID of what it uploaded, which is how release-assets takes it.
            AssertReportsTheArtifactItUploads(job, upload);

            // Nothing that gates the release can be switched off or allowed to fail.
            AssertGates(job);

            // A release artifact is built from the lockfile alone, never from a dependency cache another
            // run could have written.
            Assert.DoesNotContain(Steps(job), step =>
                step.Children.TryGetValue(new YamlScalarNode("with"), out var with) && with is YamlMappingNode inputs && Scalar(inputs, "cache") is not null);
        }
    }

    [Fact]
    public void Ci_assembles_a_release_from_exactly_the_artifacts_it_built_and_checked()
    {
        var ci = Workflow("ci.yml");
        var jobs = Jobs(ci).ToDictionary(job => job.Name, job => job.Job, StringComparer.Ordinal);
        var assembly = jobs["release-assets"];

        var built = jobs
            .Where(job => job.Key != "release-assets")
            .SelectMany(job => Steps(job.Value)
                .Where(step => Uses(step).StartsWith("actions/upload-artifact", StringComparison.Ordinal))
                .Select(step => (Job: job.Key, Artifact: Scalar(Mapping(step, "with"), "name")!)))
            .Where(upload => upload.Artifact.StartsWith("agentsplice-", StringComparison.Ordinal))
            .ToArray();

        // Every artifact a release could carry, once each, and only those — each by the ID its own job
        // reported, so one re-uploaded under the same name by any other job is never what is taken.
        var downloaded = Steps(assembly)
            .Where(step => Uses(step).StartsWith("actions/download-artifact", StringComparison.Ordinal))
            .Select(step => Mapping(step, "with"))
            .ToArray();

        Assert.All(downloaded, with => Assert.Null(Scalar(with, "name")));
        Assert.Equal(
            built.Select(upload => "${{ needs." + upload.Job + ".outputs.artifact-id }}").Order(StringComparer.Ordinal),
            downloaded.Select(with => Scalar(with, "artifact-ids")!).Order(StringComparer.Ordinal));

        var needs = Assert.IsType<YamlSequenceNode>(assembly.Children[new YamlScalarNode("needs")]).Children
            .Cast<YamlScalarNode>()
            .Select(node => node.Value)
            .ToArray();

        Assert.All(built, upload => Assert.Contains(upload.Job, needs));

        // It handles only what those jobs built: no action but the artifacts' own, nothing checked out,
        // nothing installed, nothing switched off.
        foreach (var step in Steps(assembly))
        {
            var uses = Uses(step);
            Assert.True(
                uses.Length == 0 || uses.StartsWith("actions/download-artifact@", StringComparison.Ordinal) || uses.StartsWith("actions/upload-artifact@", StringComparison.Ordinal),
                FormattableString.Invariant($"release-assets must use no action but the artifacts' own; it uses {uses}."));

            foreach (var command in InstallCommands)
            {
                Assert.DoesNotContain(command, Run(step), StringComparison.Ordinal);
            }
        }

        AssertGates(assembly);
        Assert.Contains(Steps(assembly), step => Run(step).Contains("sha256sum --check --strict", StringComparison.Ordinal));

        var release = Assert.Single(Steps(assembly), step => Uses(step).StartsWith("actions/upload-artifact", StringComparison.Ordinal));
        Assert.Equal(ReleaseArtifact, Scalar(Mapping(release, "with"), "name"));

        // The folder's contents at the artifact's root, which is the layout the release publishes from.
        Assert.Equal("release/", Scalar(Mapping(release, "with"), "path"));
        AssertReportsTheArtifactItUploads(assembly, release);

        // And the whole CI run reports that ID to the release that calls it.
        var outputs = Mapping(Mapping(Mapping(Mapping(ci, "on"), "workflow_call"), "outputs"), "release-artifact-id");
        Assert.Equal("${{ jobs.release-assets.outputs.artifact-id }}", Scalar(outputs, "value"));
    }

    [Fact]
    public void The_release_publishes_what_ci_assembled_and_nothing_else()
    {
        var jobs = Jobs(Release()).ToDictionary(job => job.Name, job => job.Job, StringComparer.Ordinal);

        Assert.Equal("./.github/workflows/ci.yml", Scalar(jobs["ci"], "uses"));
        Assert.Equal("ci", Scalar(jobs["publish"], "needs"));
        Assert.Equal("version", Scalar(jobs["ci"], "needs"));

        var steps = Steps(jobs["publish"]).ToArray();
        var download = Mapping(Assert.Single(steps, step => Uses(step).StartsWith("actions/download-artifact", StringComparison.Ordinal)), "with");
        Assert.Null(Scalar(download, "name"));
        Assert.Equal("${{ needs.ci.outputs.release-artifact-id }}", Scalar(download, "artifact-ids"));

        // The digests are verified before anything is created from them, and against exactly this tag's
        // archive for every platform.
        var verify = Array.FindIndex(steps, step => Run(step).Contains("sha256sum --check --strict", StringComparison.Ordinal));
        var create = Array.FindIndex(steps, step => Run(step).Contains("gh release create", StringComparison.Ordinal));
        Assert.InRange(verify, 0, create - 1);

        foreach (var platform in PackagePlatforms())
        {
            var extension = platform.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz";
            Assert.Contains("agentsplice-$version-" + platform + "." + extension, Run(steps[verify]), StringComparison.Ordinal);
        }

        // What the draft carries is read from the SHA256SUMS that was verified, so its assets and the
        // digests it publishes cannot drift apart.
        var command = Run(steps[create]);
        Assert.Contains("mapfile -t assets < <(awk", command, StringComparison.Ordinal);
        Assert.Contains("\"$ASSETS/SHA256SUMS\")", command, StringComparison.Ordinal);
        Assert.Contains("gh release create \"$GITHUB_REF_NAME\" \"${assets[@]}\" \"$ASSETS/SHA256SUMS\"", command, StringComparison.Ordinal);

        AssertGates(jobs["publish"]);
    }

    /// <summary>The upload step has an ID, and the job reports that step's artifact ID as its own output.</summary>
    private static void AssertReportsTheArtifactItUploads(YamlMappingNode job, YamlMappingNode upload)
    {
        var id = Scalar(upload, "id");
        Assert.False(string.IsNullOrEmpty(id), "The upload step must have an id for its artifact ID to be reported.");
        Assert.Equal("${{ steps." + id + ".outputs.artifact-id }}", Scalar(Mapping(job, "outputs"), "artifact-id"));
    }

    /// <summary>Neither the job nor any of its steps can be skipped by a condition or allowed to fail.</summary>
    private static void AssertGates(YamlMappingNode job)
    {
        Assert.False(job.Children.ContainsKey(new YamlScalarNode("if")), "A job that gates the release must not be conditional.");
        Assert.False(job.Children.ContainsKey(new YamlScalarNode("continue-on-error")), "A job that gates the release must not be allowed to fail.");

        foreach (var step in Steps(job))
        {
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("if")), FormattableString.Invariant($"The step '{Scalar(step, "name")}' gates the release and must not be conditional."));
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("continue-on-error")), FormattableString.Invariant($"The step '{Scalar(step, "name")}' gates the release and must not be allowed to fail."));
        }
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

    private static string[] PackagePlatforms()
    {
        var match = ValidateSet().Match(RepositoryPaths.ReadText(".github", "scripts", "package.ps1"));
        Assert.True(match.Success, "package.ps1 must declare the platforms it builds in a ValidateSet.");

        return match.Groups["platforms"].Value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(platform => platform.Trim('\''))
            .ToArray();
    }

    private static string Uses(YamlMappingNode step) => Scalar(step, "uses") ?? string.Empty;

    [GeneratedRegex(@"\[ValidateSet\((?<platforms>[^)]*)\)\]")]
    private static partial Regex ValidateSet();

    [GeneratedRegex(@"<VersionPrefix>(?<value>[^<]*)</VersionPrefix>")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"<VersionSuffix>(?<value>[^<]*)</VersionSuffix>")]
    private static partial Regex VersionSuffix();
}
