using System.Text.RegularExpressions;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// The image reaches the registry only when a person publishes a release, as exactly the archive that
/// release carries, by jobs that each write one thing and run nothing they fetched (ADR 0022).
/// </summary>
/// <remarks>
/// None of this runs until a release is published, and a published image cannot be taken back: a
/// public package never becomes private again. So the workflow files are read and held to those
/// properties on every build, as the release workflow is.
/// </remarks>
public sealed partial class ImagePublicationContractTests
{
    [Fact]
    public void The_registry_is_reached_only_when_a_release_is_published()
    {
        var on = Mapping(PublishImage(), "on");

        Assert.Equal(["release", "workflow_dispatch"], on.Children.Keys.Cast<YamlScalarNode>().Select(key => key.Value!).Order(StringComparer.Ordinal));

        var types = Assert.IsType<YamlSequenceNode>(Mapping(on, "release").Children[new YamlScalarNode("types")]);
        Assert.Equal(["published"], types.Children.Cast<YamlScalarNode>().Select(type => type.Value));

        var tag = Mapping(Mapping(Mapping(on, "workflow_dispatch"), "inputs"), "tag");
        Assert.Equal("true", Scalar(tag, "required"));
    }

    [Fact]
    public void Each_job_that_writes_writes_one_kind_of_thing()
    {
        var workflow = PublishImage();

        Assert.Equal("read", Scalar(Mapping(workflow, "permissions"), "contents"));
        Assert.Single(Mapping(workflow, "permissions").Children);

        var jobs = Jobs(workflow).ToDictionary(job => job.Name, job => job.Job, StringComparer.Ordinal);
        Assert.Equal(["notes", "push"], jobs.Keys.Order(StringComparer.Ordinal));

        Assert.Equal(
            ["contents=read", "packages=write"],
            Permissions(jobs["push"]).Order(StringComparer.Ordinal));
        Assert.Equal(["contents=write"], Permissions(jobs["notes"]));
        Assert.Equal("push", Scalar(jobs["notes"], "needs"));
    }

    [Fact]
    public void The_publishing_jobs_use_no_action_and_build_or_install_nothing()
    {
        string[] forbidden = ["docker build ", "buildx build", "docker commit", "pnpm", "npm ", "dotnet "];

        foreach (var (name, job) in Jobs(PublishImage()))
        {
            foreach (var step in Steps(job))
            {
                Assert.True(Scalar(step, "uses") is null, FormattableString.Invariant($"The '{name}' job must run no action; it uses {Scalar(step, "uses")}."));

                foreach (var command in forbidden)
                {
                    Assert.DoesNotContain(command, Run(step), StringComparison.Ordinal);
                }
            }
        }
    }

    [Fact]
    public void The_push_verifies_and_reads_the_archive_before_loading_it_and_pushes_once()
    {
        var push = Jobs(PublishImage()).Single(job => job.Name == "push").Job;
        var script = string.Join('\n', Steps(push).Select(Run));

        var download = script.IndexOf("gh release download", StringComparison.Ordinal);
        var verify = script.IndexOf("sha256sum --check --strict", StringComparison.Ordinal);
        var manifest = script.IndexOf("manifest.json", StringComparison.Ordinal);
        var load = script.IndexOf("docker load", StringComparison.Ordinal);

        Assert.True(download >= 0 && download < verify && verify < manifest && manifest < load, "The archive must be downloaded, verified, and read before it is loaded.");
        Assert.Single(DockerPush().Matches(script));
        Assert.Contains("refusing to move it", script, StringComparison.Ordinal);
        Assert.Contains(".draft == false", script, StringComparison.Ordinal);
    }

    [Fact]
    public void No_workflow_puts_an_expression_into_a_script()
    {
        // A tag name or an input interpolated into a script becomes part of it; through env it stays data.
        foreach (var file in WorkflowFiles())
        {
            foreach (var (name, job) in Jobs(Workflow(file)))
            {
                foreach (var step in Steps(job))
                {
                    Assert.False(
                        Run(step).Contains("${{", StringComparison.Ordinal),
                        FormattableString.Invariant($"A step of '{name}' in {file} interpolates an expression into its script."));
                }
            }
        }
    }

    [Fact]
    public void Nothing_is_tagged_or_pushed_as_latest()
    {
        foreach (var file in WorkflowFiles())
        {
            foreach (var (_, job) in Jobs(Workflow(file)))
            {
                Assert.All(Steps(job), step => Assert.DoesNotContain(":latest", Run(step), StringComparison.Ordinal));
            }
        }
    }

    [Fact]
    public void The_jobs_that_build_load_and_push_the_image_run_on_the_classic_image_store()
    {
        var ci = Jobs(Workflow("ci.yml")).ToDictionary(job => job.Name, job => job.Job, StringComparer.Ordinal);
        var push = Jobs(PublishImage()).Single(job => job.Name == "push").Job;

        foreach (var job in new[] { ci["image"], ci["image-archive"], push })
        {
            Assert.Equal("ubuntu-24.04", Scalar(job, "runs-on"));
        }

        Assert.Contains(Steps(ci["image"]), step => Run(step).Contains("io.containerd.snapshotter", StringComparison.Ordinal));
    }

    [Fact]
    public void The_image_ci_tests_is_the_image_it_saves()
    {
        var steps = Steps(Jobs(Workflow("ci.yml")).Single(job => job.Name == "image").Job).ToArray();

        int Index(string text) => Array.FindIndex(steps, step => Run(step).Contains(text, StringComparison.Ordinal));

        var build = Index("docker build --provenance=false --sbom=false");
        var start = Index("docker compose up --no-build");
        var smoke = Index("smoke-gateway.sh");
        var save = Index("docker save");

        Assert.True(build >= 0 && build < start && start < smoke && smoke < save, "The image must be built, started without a rebuild, smoke-tested, and only then saved.");
        Assert.Contains("com.docker.compose.", Run(steps[save]), StringComparison.Ordinal);
        Assert.DoesNotContain(steps, step => Run(step).Contains("up --build", StringComparison.Ordinal));
    }

    [Fact]
    public void Compose_names_an_image_no_registry_namespace_can_claim_and_builds_it()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText("docker-compose.yml")));

        var service = Mapping(Mapping((YamlMappingNode)stream.Documents[0].RootNode, "services"), "agentsplice");
        var image = Scalar(service, "image")!;

        // A name with no '/' is in Docker Hub's library namespace, which no account can publish to; a
        // name like 'agentsplice/agentsplice' is an account anyone could register.
        Assert.DoesNotContain('/', image);
        Assert.Equal("build", Scalar(service, "pull_policy"));

        var ci = Jobs(Workflow("ci.yml")).Single(job => job.Name == "image").Job;
        Assert.Contains(Steps(ci), step => Run(step).Contains("--tag " + image + " ", StringComparison.Ordinal));
    }

    [Fact]
    public void The_release_carries_the_archive_of_the_image_ci_ran_as_users_do()
    {
        var ci = Jobs(Workflow("ci.yml")).ToDictionary(job => job.Name, job => job.Job, StringComparer.Ordinal);
        var assembly = ci["release-assets"];

        // Only once image-archive has loaded the archive elsewhere and run it the way the guide does.
        var needs = Assert.IsType<YamlSequenceNode>(assembly.Children[new YamlScalarNode("needs")]).Children
            .Cast<YamlScalarNode>()
            .Select(node => node.Value);
        Assert.Contains("image-archive", needs);

        const string Archive = "agentsplice-$version-image-linux-amd64.tar.gz";
        Assert.Contains(Steps(assembly), step => Run(step).Contains(Archive, StringComparison.Ordinal));

        // In the step that verifies what the draft is made of, not merely in the notes.
        var publish = Jobs(Workflow("release.yml")).Single(job => job.Name == "publish").Job;
        var verify = Assert.Single(Steps(publish), step => Run(step).Contains("sha256sum --check --strict", StringComparison.Ordinal));
        Assert.Contains(Archive, Run(verify), StringComparison.Ordinal);
    }

    [Fact]
    public void The_installation_guide_runs_the_image_as_ci_does()
    {
        var guide = DockerRunFlags(RepositoryPaths.ReadText("docs", "INSTALLATION.md"), "bash");
        var ci = DockerRunFlags(
            string.Join('\n', Steps(Jobs(Workflow("ci.yml")).Single(job => job.Name == "image-archive").Job).Select(Run)),
            null);

        Assert.NotEmpty(guide);
        Assert.Equal(guide.Order(StringComparer.Ordinal), ci.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Every_port_the_guides_publish_is_on_loopback()
    {
        foreach (var guide in new[] { RepositoryPaths.ReadText("README.md"), RepositoryPaths.ReadText("docs", "INSTALLATION.md") })
        {
            foreach (Match publish in PublishFlag().Matches(guide))
            {
                Assert.StartsWith("127.0.0.1:", publish.Groups["binding"].Value, StringComparison.Ordinal);
            }
        }
    }

    /// <summary>The flags of the first <c>docker run</c>, from a guide's code block in a language or from a script.</summary>
    private static string[] DockerRunFlags(string text, string? language)
    {
        var source = language is null
            ? text
            : string.Join('\n', CodeBlock().Matches(text).Where(block => block.Groups["language"].Value == language).Select(block => block.Groups["body"].Value));

        var run = DockerRun().Match(source.Replace("\r\n", "\n", StringComparison.Ordinal));
        Assert.True(run.Success, "No docker run command found.");

        return Flag().Matches(run.Value.Replace("\\\n", " ", StringComparison.Ordinal))
            .Select(flag => flag.Value)
            .Where(flag => !flag.Contains("$token", StringComparison.Ordinal))
            .ToArray();
    }

    private static string[] WorkflowFiles() =>
        Directory.GetFiles(RepositoryPaths.Resolve(".github", "workflows"), "*.yml").Select(path => Path.GetFileName(path)!).ToArray();

    private static YamlMappingNode PublishImage() => Workflow("publish-image.yml");

    private static YamlMappingNode Workflow(string fileName)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText(".github", "workflows", fileName)));

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static IEnumerable<string> Permissions(YamlMappingNode job) =>
        Mapping(job, "permissions").Children.Select(entry => ((YamlScalarNode)entry.Key).Value + "=" + ((YamlScalarNode)entry.Value).Value);

    private static IEnumerable<(string Name, YamlMappingNode Job)> Jobs(YamlMappingNode workflow) =>
        Mapping(workflow, "jobs").Children.Select(entry =>
            (((YamlScalarNode)entry.Key).Value!, Assert.IsType<YamlMappingNode>(entry.Value)));

    private static IEnumerable<YamlMappingNode> Steps(YamlMappingNode job) =>
        job.Children.TryGetValue(new YamlScalarNode("steps"), out var steps)
            ? Assert.IsType<YamlSequenceNode>(steps).Children.Cast<YamlMappingNode>()
            : [];

    private static string Run(YamlMappingNode step) => Scalar(step, "run") ?? string.Empty;

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    [GeneratedRegex(@"\bdocker push\b")]
    private static partial Regex DockerPush();

    [GeneratedRegex(@"```(?<language>[a-z]*)\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"docker run(?:[^\n]*\\\n)*[^\n]*")]
    private static partial Regex DockerRun();

    [GeneratedRegex(@"--(?:publish|volume|add-host|env|name|detach)(?:[ =](?!-)(?:'[^']*'|[^\s\\]+))?")]
    private static partial Regex Flag();

    [GeneratedRegex(@"--publish[ =]""?(?<binding>[^\s""]+)")]
    private static partial Regex PublishFlag();
}
