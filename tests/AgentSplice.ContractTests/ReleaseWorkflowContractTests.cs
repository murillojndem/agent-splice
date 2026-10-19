using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// A tag becomes a reviewed draft, built and assembled by the full CI suite, published by the one job
/// that may write (ADR 0018, ADR 0021, ADR 0029).
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

    /// <summary>What a job that only handles artifacts, or that holds a write token, never runs.</summary>
    internal static readonly string[] InstallCommands = ["pnpm", "npm ", "npx ", "dotnet ", "apt-get", "pip "];

    /// <summary>
    /// The platforms whose package gates nothing: its job may fail without failing CI or holding a
    /// release back, and a release carries its archive only when that job uploaded one (ADR 0029, launch
    /// decision D3).
    /// </summary>
    private static readonly string[] OptionalPlatforms = ["osx-arm64"];

    /// <summary>The command that clears macOS's quarantine mark from a whole folder.</summary>
    private const string ClearQuarantine = "xattr -dr com.apple.quarantine";

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

            // And nothing more: packages: write here would let a pushed tag reach the registry before
            // anyone reviewed the draft (ADR 0022).
            Assert.Equal(
                ["contents=write"],
                permissions!.Children.Select(entry => ((YamlScalarNode)entry.Key).Value + "=" + (entry.Value as YamlScalarNode)?.Value));

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
        Assert.Contains("osx-arm64", platforms);
        Assert.Subset(platforms.ToHashSet(StringComparer.Ordinal), OptionalPlatforms.ToHashSet(StringComparer.Ordinal));

        foreach (var platform in platforms)
        {
            var job = PackageJob(jobs, platform).Job;

            Assert.Single(Steps(job), step => Run(step).Contains("smoke-package.ps1", StringComparison.Ordinal));

            var upload = Assert.Single(Steps(job), step => Uses(step).StartsWith("actions/upload-artifact", StringComparison.Ordinal));
            Assert.Equal("agentsplice-" + platform, Scalar(Mapping(upload, "with"), "name"));

            // The job reports the ID of what it uploaded, which is how release-assets takes it.
            AssertReportsTheArtifactItUploads(job, upload);

            // Nothing that gates the release can be switched off or allowed to fail; a package that gates
            // nothing may fail, but never skip a check before its upload.
            if (OptionalPlatforms.Contains(platform, StringComparer.Ordinal))
            {
                AssertMayFailButSkipsNoCheck(job);
            }
            else
            {
                AssertGates(job);
            }

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

        // Every job the release waits for decides it: none can be switched off or allowed to fail,
        // including the ones that only check what another built, such as image-archive. The one
        // exception is the package of a platform that gates nothing, which may fail but skips no check.
        var optional = OptionalPlatforms
            .Select(platform => (Platform: platform, Job: PackageJob(Jobs(ci), platform).Name))
            .ToArray();

        foreach (var gate in needs)
        {
            if (optional.Any(entry => entry.Job == gate))
            {
                AssertMayFailButSkipsNoCheck(jobs[gate!]);
            }
            else
            {
                AssertGates(jobs[gate!]);
            }
        }

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

        // No step can be skipped or allowed to fail but the download of an optional package, which runs
        // exactly when its job reported an ID: without one, download-artifact would take every artifact
        // of the run. The verification is told that ID, so it expects that archive exactly when it came.
        Assert.False(assembly.Children.ContainsKey(new YamlScalarNode("if")), "release-assets must not be conditional.");
        Assert.False(assembly.Children.ContainsKey(new YamlScalarNode("continue-on-error")), "release-assets must not be allowed to fail.");

        var verify = Assert.Single(Steps(assembly), step => Run(step).Contains("sha256sum --check --strict", StringComparison.Ordinal));

        foreach (var step in Steps(assembly))
        {
            var reference = Uses(step).StartsWith("actions/download-artifact", StringComparison.Ordinal)
                ? Scalar(Mapping(step, "with"), "artifact-ids")
                : null;
            var job = optional.SingleOrDefault(entry => reference == "${{ needs." + entry.Job + ".outputs.artifact-id }}").Job;
            var condition = Scalar(step, "if");

            Assert.True(
                job is null ? condition is null or "always()" : condition == "needs." + job + ".outputs.artifact-id != ''",
                FormattableString.Invariant($"The step '{Scalar(step, "name")}' of release-assets has the condition '{condition}'."));
            Assert.False(step.Children.ContainsKey(new YamlScalarNode("continue-on-error")), FormattableString.Invariant($"The step '{Scalar(step, "name")}' gates the release and must not be allowed to fail."));
        }

        foreach (var (platform, job) in optional)
        {
            Assert.Contains("${{ needs." + job + ".outputs.artifact-id }}", Mapping(verify, "env").Children.Values.OfType<YamlScalarNode>().Select(value => value.Value));
            Assert.Contains("agentsplice-$version-" + platform + ".tar.gz", Run(verify), StringComparison.Ordinal);
        }

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
    /// <remarks>
    /// A step may run <c>always()</c>, as a cleanup step does: that runs it after a failure too, and the
    /// failure still fails the job.
    /// </remarks>
    private static void AssertGates(YamlMappingNode job)
    {
        Assert.False(job.Children.ContainsKey(new YamlScalarNode("continue-on-error")), "A job that gates the release must not be allowed to fail.");
        AssertRunsEveryStep(job);
    }

    /// <summary>
    /// The job of a package that gates nothing (ADR 0029): the job itself may fail without failing the
    /// run, and nothing else. It always runs; it has a timeout, because release-assets waits for it; and
    /// no step of it can be skipped or allowed to fail, with the upload last, so its artifact — and the
    /// ID release-assets decides by — exists only if every check passed.
    /// </summary>
    private static void AssertMayFailButSkipsNoCheck(YamlMappingNode job)
    {
        Assert.Equal("true", Scalar(job, "continue-on-error"));
        Assert.True(
            int.TryParse(Scalar(job, "timeout-minutes"), out var minutes) && minutes is > 0 and <= 60,
            "A job the release's assembly waits for, without being gated by it, must have a timeout of an hour or less.");
        AssertRunsEveryStep(job);
        Assert.StartsWith("actions/upload-artifact", Uses(Steps(job).Last()), StringComparison.Ordinal);
    }

    /// <summary>The job always runs, and none of its steps can be skipped by a condition or allowed to fail.</summary>
    private static void AssertRunsEveryStep(YamlMappingNode job)
    {
        Assert.False(job.Children.ContainsKey(new YamlScalarNode("if")), "A job that gates the release must not be conditional.");

        foreach (var step in Steps(job))
        {
            Assert.True(
                Scalar(step, "if") is null or "always()",
                FormattableString.Invariant($"The step '{Scalar(step, "name")}' gates the release and must not be conditional."));
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
        using var package = JsonDocument.Parse(RepositoryPaths.ReadText("web", "AgentSplice.Dashboard", "package.json"));

        Assert.Equal(GatewayVersion(), package.RootElement.GetProperty("version").GetString());
    }

    [Theory]
    [InlineData("ci.yml")]
    [InlineData("release.yml")]
    [InlineData("publish-image.yml")]
    public void No_linux_job_runs_on_a_label_github_moves(string fileName)
    {
        // ubuntu-latest moves to Ubuntu 26.04 from 2026-10-19. A new build image is a change to
        // review, not one to discover in a release run (ADR 0027).
        foreach (var (name, job) in Jobs(Workflow(fileName)))
        {
            var runsOn = Scalar(job, "runs-on");

            var labels = runsOn == "${{ matrix.os }}"
                ? Assert.IsType<YamlSequenceNode>(Mapping(Mapping(job, "strategy"), "matrix").Children[new YamlScalarNode("os")])
                    .Children.OfType<YamlScalarNode>().Select(label => label.Value!)
                : [runsOn ?? string.Empty];

            Assert.All(labels, label => Assert.False(
                label.Equals("ubuntu-latest", StringComparison.Ordinal),
                FormattableString.Invariant($"{fileName}: job '{name}' runs on ubuntu-latest.")));
        }
    }

    [Fact]
    public void The_draft_notes_list_known_limitations_and_when_the_runtime_stops_being_patched()
    {
        // A pre-release is read by people deciding whether to run it; what it cannot do and how long
        // its runtime is patched are what they need first. The date is the one the guide gives.
        var publish = Jobs(Release()).Single(job => job.Name == "publish").Job;
        var notes = string.Join('\n', Steps(publish).Select(Run));

        Assert.Contains("**Known limitations.**", notes, StringComparison.Ordinal);
        Assert.Contains("November 14, 2028", notes, StringComparison.Ordinal);

        // A draft may lack the macOS package, which gates nothing (ADR 0029); whoever publishes it must be
        // told, rather than find out from the assets.
        Assert.Contains("This release has no macOS package", notes, StringComparison.Ordinal);
        Assert.Contains("November 14, 2028", RepositoryPaths.ReadText("docs", "INSTALLATION.md"), StringComparison.Ordinal);
        Assert.Contains("November 14, 2028", RepositoryPaths.ReadText(".github", "scripts", "package.ps1"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("docs/INSTALLATION.md")]
    [InlineData("README.md")]
    public void The_guides_name_the_version_the_build_carries(string guide)
    {
        // Each command names a package file, an image tag, or a release's download address. A version
        // bump a guide missed would hand out commands that download nothing, and nothing else would
        // notice; the README's quickstart is the first of them a visitor copies.
        var named = GuideVersion()
            .Matches(RepositoryPaths.ReadText(guide.Split('/')))
            .Select(match => match.Groups["version"].Value)
            .ToArray();

        Assert.NotEmpty(named);
        Assert.All(named, version => Assert.Equal(GatewayVersion(), version));
    }

    [Fact]
    public void The_macOS_package_is_tested_on_the_oldest_macOS_its_documents_state()
    {
        // The documents name the oldest macOS the package runs on, the smoke test holds every native file
        // in it to that version, and CI runs it there, so the floor is proven by a run rather than read
        // off the binaries alone (ADR 0029). A label GitHub moves, such as macos-latest, would carry the
        // proof to another version without anyone deciding it.
        var enforced = EnforcedMacosFloor().Match(RepositoryPaths.ReadText(".github", "scripts", "smoke-package.ps1"));

        Assert.True(enforced.Success, "smoke-package.ps1 must state the macOS floor it enforces.");

        var floor = Version.Parse(enforced.Groups["version"].Value);
        Assert.Equal(0, floor.Minor);
        Assert.Equal("macos-" + floor.Major.ToString(CultureInfo.InvariantCulture), Scalar(PackageJob(Jobs(Workflow("ci.yml")), "osx-arm64").Job, "runs-on"));

        // What tells a user the floor says the same; the README only once it offers the package.
        string[][] stating =
        [
            ["docs", "INSTALLATION.md"],
            [".github", "scripts", "package.ps1"],
            [".github", "workflows", "release.yml"],
        ];

        foreach (var document in stating.Append(["README.md"]))
        {
            var stated = StatedMacosFloor()
                .Matches(RepositoryPaths.ReadText(document))
                .Select(match => match.Groups["version"].Value)
                .Distinct()
                .ToArray();

            Assert.True(
                stated.All(version => version == floor.Major.ToString(CultureInfo.InvariantCulture)) && (stated.Length == 1 || (stated.Length == 0 && document[0] == "README.md")),
                $"{string.Join('/', document)} states the macOS floor as [{string.Join(", ", stated)}]; the smoke test enforces {floor.Major}.");
        }
    }

    [Fact]
    public void Every_document_that_offers_the_macOS_package_says_how_to_clear_the_quarantine_mark()
    {
        // The package is signed ad hoc and not notarized, so a browser's download of it does not open until
        // the quarantine mark is cleared from the whole folder, SQLite's library included: without -r the
        // library stays marked (ADR 0029). The smoke test runs the very command the documents give.
        Assert.Contains(ClearQuarantine, RepositoryPaths.ReadText(".github", "scripts", "smoke-package.ps1"), StringComparison.Ordinal);

        string[][] documents =
        [
            ["README.md"],
            ["docs", "INSTALLATION.md"],
            [".github", "scripts", "package.ps1"],
            [".github", "workflows", "release.yml"],
        ];

        var offering = documents
            .Select(document => (Name: string.Join('/', document), Text: RepositoryPaths.ReadText(document)))
            .Where(document => document.Text.Contains("osx-arm64", StringComparison.Ordinal))
            .ToArray();

        // The guide, the package's README, and the release notes always do; the README once it offers it.
        Assert.True(offering.Length >= 3, FormattableString.Invariant($"Only {offering.Length} documents offer the macOS package."));
        Assert.All(offering, document => Assert.True(
            document.Text.Contains(ClearQuarantine, StringComparison.Ordinal),
            $"{document.Name} offers the macOS package without saying how to clear the quarantine mark ({ClearQuarantine})."));
    }

    private static string GatewayVersion()
    {
        var props = RepositoryPaths.ReadText("Directory.Build.props");
        var prefix = VersionPrefix().Match(props).Groups["value"].Value;
        var suffix = VersionSuffix().Match(props).Groups["value"].Value;

        Assert.False(string.IsNullOrEmpty(prefix));

        return suffix.Length == 0 ? prefix : prefix + "-" + suffix;
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

    /// <summary>The one CI job that builds the platform's package.</summary>
    private static (string Name, YamlMappingNode Job) PackageJob(IEnumerable<(string Name, YamlMappingNode Job)> jobs, string platform) =>
        Assert.Single(jobs, entry => Steps(entry.Job).Any(step =>
            Run(step).Contains("package.ps1 -RuntimeIdentifier " + platform + " ", StringComparison.Ordinal)));

    [GeneratedRegex(@"\$macosFloor = \[version\]'(?<version>[0-9]+\.[0-9]+)'")]
    private static partial Regex EnforcedMacosFloor();

    [GeneratedRegex(@"macOS (?<version>[0-9]+(?:\.[0-9]+)?) or later")]
    private static partial Regex StatedMacosFloor();

    [GeneratedRegex(@"\[ValidateSet\((?<platforms>[^)]*)\)\]")]
    private static partial Regex ValidateSet();

    [GeneratedRegex(@"<VersionPrefix>(?<value>[^<]*)</VersionPrefix>")]
    private static partial Regex VersionPrefix();

    [GeneratedRegex(@"<VersionSuffix>(?<value>[^<]*)</VersionSuffix>")]
    private static partial Regex VersionSuffix();

    // A package file name, agentsplice-<version>-<platform>, the platform win-x64, linux-x64, or osx-arm64;
    // an image tag, agent-splice:<version>; or a release's download address, releases/download/v<version>/.
    [GeneratedRegex(@"(?:agentsplice-|agent-splice:|releases/download/v)(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.]+)?)(?=-(?:win-x64|linux-x64|osx-arm64)|[\s`'""/]|$)", RegexOptions.Multiline)]
    private static partial Regex GuideVersion();
}
