using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
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
    /// <summary>The archive a release carries, as every step that makes, checks, or pushes it spells it.</summary>
    private const string Archive = "agentsplice-$version-image-linux-amd64.tar.gz";

    /// <summary>The name the image is saved, loaded, and pushed under, as every step spells it.</summary>
    private const string Reference = "ghcr.io/${GITHUB_REPOSITORY,,}:$version";

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
    public void No_job_but_the_push_job_can_reach_the_registry()
    {
        // Any job of any workflow can ask for packages: write, and the image job already holds an image
        // under the registry's name: the property holds only if no other job can push it.
        string[] registryCommands = ["docker login", "docker push", "docker image push", "imagetools create", "skopeo ", "oras push"];

        foreach (var file in WorkflowFiles())
        {
            var workflow = Workflow(file);

            // Declared, so that the repository's default for a workflow without permissions never applies.
            Assert.True(workflow.Children.ContainsKey(new YamlScalarNode("permissions")), FormattableString.Invariant($"{file} must declare its permissions."));
            AssertGrantsNoPackageWrite(workflow, file);

            foreach (var (name, job) in Jobs(workflow))
            {
                var pushes = file == "publish-image.yml" && name == "push";
                if (pushes)
                {
                    continue;
                }

                AssertGrantsNoPackageWrite(job, FormattableString.Invariant($"The '{name}' job of {file}"));

                foreach (var step in Steps(job))
                {
                    foreach (var command in registryCommands)
                    {
                        Assert.False(
                            Run(step).Contains(command, StringComparison.Ordinal),
                            FormattableString.Invariant($"The '{name}' job of {file} runs '{command.Trim()}'; only publish-image.yml's push job reaches the registry."));
                    }
                }
            }
        }
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
    public void The_publishing_jobs_run_nothing_fetched_and_build_tag_or_install_nothing()
    {
        // With the registry token in reach: nothing installed or downloaded to run, no image run that a
        // registry served, no image built, and no name but the archive's own (ADR 0022 decision 4).
        string[] forbidden =
        [
            .. ReleaseWorkflowContractTests.InstallCommands,
            "apt ",
            "curl ",
            "wget ",
            "docker build ",
            "buildx build",
            "docker commit",
            "docker run",
            "docker create",
            "docker pull",
            "docker tag",
            "docker image tag",
            "imagetools create",
            "--all-tags",
        ];

        foreach (var (name, job) in Jobs(PublishImage()))
        {
            // A job-level action, container, or service would run all of it inside something fetched.
            foreach (var key in new[] { "uses", "container", "services" })
            {
                Assert.False(job.Children.ContainsKey(new YamlScalarNode(key)), FormattableString.Invariant($"The '{name}' job must not declare '{key}'."));
            }

            Assert.NotEmpty(Steps(job));

            foreach (var step in Steps(job))
            {
                Assert.True(Scalar(step, "uses") is null, FormattableString.Invariant($"The '{name}' job must run no action; it uses {Scalar(step, "uses")}."));

                foreach (var command in forbidden)
                {
                    Assert.False(
                        Run(step).Contains(command, StringComparison.Ordinal),
                        FormattableString.Invariant($"A step of the '{name}' job runs '{command.Trim()}'."));
                }
            }
        }
    }

    [Fact]
    public void The_push_checks_the_release_and_the_archive_before_loading_it()
    {
        var steps = Steps(Jobs(PublishImage()).Single(job => job.Name == "push").Job).ToArray();

        // Published, immutable, and both the release and its image made by GitHub Actions rather than
        // uploaded by hand — named, and with the name it pushes, exactly as CI saved it.
        var release = Run(Assert.Single(steps, step => Run(step).Contains("releases/tags/$TAG", StringComparison.Ordinal)));
        Assert.Contains(".draft == false", release, StringComparison.Ordinal);
        Assert.Contains(".author.login == \"github-actions[bot]\"", release, StringComparison.Ordinal);
        Assert.Contains(".uploader.login == \"github-actions[bot]\"", release, StringComparison.Ordinal);
        Assert.Contains(".immutable == true", release, StringComparison.Ordinal);
        Assert.Contains("asset=\"" + Archive + "\"", release, StringComparison.Ordinal);
        Assert.Contains("reference=" + Reference, release, StringComparison.Ordinal);

        // Downloaded, checked against SHA256SUMS and against the digest GitHub recorded, and read, all
        // before anything is loaded.
        var script = string.Join('\n', steps.Select(Run));
        var download = script.IndexOf("gh release download", StringComparison.Ordinal);
        var verify = script.IndexOf("sha256sum --check --strict", StringComparison.Ordinal);
        var recorded = script.IndexOf("select(.name == $asset) | .digest", StringComparison.Ordinal);
        var manifest = script.IndexOf("manifest.json", StringComparison.Ordinal);
        var load = script.IndexOf("docker load", StringComparison.Ordinal);

        Assert.True(
            download >= 0 && download < verify && verify < recorded && recorded < manifest && manifest < load,
            "The archive must be downloaded, verified against both digests, and read before it is loaded.");
    }

    [Fact]
    public void The_push_never_moves_a_tag_and_names_the_digest_it_checked()
    {
        var script = string.Join('\n', Steps(Jobs(PublishImage()).Single(job => job.Name == "push").Job).Select(Run));

        // One push, of the archive's own name, and only on an answer: the tag is not there, or this token
        // cannot read the package. Any other failure of the lookup stops the job instead.
        Assert.Single(DockerPush().Matches(script));
        Assert.Matches(PushOnAnAnswer(), script);
        Assert.Contains("2> \"$RUNNER_TEMP/lookup.err\"", script, StringComparison.Ordinal);
        Assert.Contains("grep -q ': not found$' \"$RUNNER_TEMP/lookup.err\"", script, StringComparison.Ordinal);
        Assert.Contains("refusing to move it", script, StringComparison.Ordinal);
        Assert.Contains("refusing to push", script, StringComparison.Ordinal);

        // The manifest is fetched by the digest the notes will name, and must hash to it.
        Assert.Contains("--raw \"${REFERENCE%:*}@$digest\" > \"$RUNNER_TEMP/pushed.json\"", script, StringComparison.Ordinal);
        Assert.Contains("sha256sum < \"$RUNNER_TEMP/pushed.json\"", script, StringComparison.Ordinal);
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
    public void No_workflow_names_latest()
    {
        // Anywhere, not only in scripts: a value passed through env is how a step would spell it.
        foreach (var file in WorkflowFiles())
        {
            foreach (var value in Scalars(Workflow(file)))
            {
                Assert.False(value.Contains(":latest", StringComparison.Ordinal), FormattableString.Invariant($"{file} names ':latest': {value}"));
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

        // The pinned runner fixes the operating system, not the Docker it is updated to: the image job
        // refuses the containerd store, and does so before it builds anything.
        var steps = Steps(ci["image"]).ToArray();
        var check = Array.FindIndex(steps, step => Run(step).Contains("io.containerd.snapshotter", StringComparison.Ordinal));
        var build = Array.FindIndex(steps, step => DockerBuild().IsMatch(Run(step)));
        Assert.True(check >= 0 && check <= build, "The image job must check the image store before it builds.");

        var run = Run(steps[check]);
        var refusal = run.IndexOf("exit 1", run.IndexOf("io.containerd.snapshotter", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.True(refusal >= 0, "The image job must stop on the containerd store, not merely report it.");
        Assert.True(check < build || refusal < DockerBuild().Match(run).Index, "The image job must stop before it builds.");
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

        // Built once, and by docker: a second build, or one by Compose, would make what is saved an image
        // the checks never ran against.
        Assert.Single(steps, step => DockerBuild().IsMatch(Run(step)));
        Assert.DoesNotContain(steps, step => Run(step).Contains("docker compose build", StringComparison.Ordinal) || Run(step).Contains("up --build", StringComparison.Ordinal));

        // What is saved is the image the started container runs, under the name the release pushes.
        var saving = Run(steps[save]);
        var image = saving.IndexOf("docker inspect --format '{{.Image}}' \"$container\"", StringComparison.Ordinal);
        var tag = saving.IndexOf("docker tag \"$image\" \"$reference\"", StringComparison.Ordinal);
        var saved = saving.IndexOf("docker save \"$reference\"", StringComparison.Ordinal);

        Assert.True(image >= 0 && image < tag && tag < saved, "The save step must save the running container's image, tagged for the registry.");
        Assert.Contains("reference=\"" + Reference + "\"", saving, StringComparison.Ordinal);
        Assert.Contains("archive=\"" + Archive + "\"", saving, StringComparison.Ordinal);
        Assert.Contains("com.docker.compose.", saving, StringComparison.Ordinal);
    }

    [Fact]
    public void Compose_names_an_image_no_registry_namespace_can_claim_and_builds_it()
    {
        var service = Mapping(Mapping(Compose(), "services"), "agentsplice");
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

        Assert.Contains(Steps(assembly), step => Run(step).Contains(Archive, StringComparison.Ordinal));

        // image-archive loads it under the name the image job saved it with and the push job pushes.
        Assert.Contains(Steps(ci["image-archive"]), step => Run(step).Contains("reference=\"" + Reference + "\"", StringComparison.Ordinal));

        // In the step that verifies what the draft is made of, not merely in the notes.
        var publish = Jobs(Workflow("release.yml")).Single(job => job.Name == "publish").Job;
        var verify = Assert.Single(Steps(publish), step => Run(step).Contains("sha256sum --check --strict", StringComparison.Ordinal));
        Assert.Contains(Archive, Run(verify), StringComparison.Ordinal);
    }

    [Fact]
    public void The_installation_guide_runs_the_image_as_ci_does()
    {
        var ci = CiDockerRun();

        var guide = CodeBlocks(RepositoryPaths.ReadText("docs", "INSTALLATION.md"))
            .Select(block => (block.Language, Commands: DockerCommands(block.Body, Continuation(block.Language)).ToArray()))
            .Where(block => block.Commands.Length > 0)
            .ToArray();

        // The bash command and the PowerShell one, each word for word what CI runs, but for the image.
        Assert.Equal(["bash", "powershell"], guide.Select(block => block.Language).Order(StringComparer.Ordinal));

        foreach (var (_, commands) in guide)
        {
            var command = Assert.Single(commands);
            Assert.Equal(ci[..^1], command[..^1]);
            Assert.StartsWith(PublishedImage() + ":", command[^1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void The_readme_runs_the_image_as_ci_does()
    {
        // The quickstart repeats the guide's bash command so that a visitor need not leave the page. A flag
        // dropped or changed there would start a container CI never ran: without the volume, a store that
        // goes with the container; without the token, a gateway that exits at startup.
        var ci = CiDockerRun();

        var commands = CodeBlocks(RepositoryPaths.ReadText("README.md"))
            .SelectMany(block => DockerCommands(block.Body, Continuation(block.Language)))
            .ToArray();

        // Not vacuous: a quickstart whose command the extraction missed would pass with nothing compared.
        Assert.NotEmpty(commands);

        foreach (var command in commands)
        {
            Assert.Equal(ci[..^1], command[..^1]);
            Assert.StartsWith(PublishedImage() + ":", command[^1], StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Every_port_the_guides_and_compose_publish_is_on_loopback()
    {
        var bindings = new List<string>();

        foreach (var (path, text) in Documents())
        {
            foreach (var (language, body) in CodeBlocks(text))
            {
                foreach (var command in DockerCommands(body, Continuation(language)))
                {
                    bindings.AddRange(Published(command, FormattableString.Invariant($"A docker command in {path}")));
                }
            }
        }

        foreach (var service in Mapping(Compose(), "services").Children)
        {
            var name = ((YamlScalarNode)service.Key).Value;
            var definition = (YamlMappingNode)service.Value;

            Assert.False(Scalar(definition, "network_mode") == "host", FormattableString.Invariant($"Compose's {name} runs on the host's network."));

            if (definition.Children.TryGetValue(new YamlScalarNode("ports"), out var ports))
            {
                bindings.AddRange(Assert.IsType<YamlSequenceNode>(ports).Children.Select(port => port switch
                {
                    YamlScalarNode scalar => scalar.Value ?? string.Empty,
                    YamlMappingNode mapping => (Scalar(mapping, "host_ip") ?? string.Empty) + ":" + Scalar(mapping, "published"),
                    _ => string.Empty,
                }));
            }
        }

        Assert.NotEmpty(bindings);
        Assert.All(bindings, binding => Assert.StartsWith("127.0.0.1:", binding, StringComparison.Ordinal));
    }

    [Fact]
    public void Every_image_name_the_documents_give_is_this_repositorys()
    {
        // The workflows derive the name from the repository; the documents spell it out, and after a
        // rename or a transfer they would send users to a namespace the project no longer owns. A trace
        // report is the exception: it records the image one session ran, and is never edited afterwards
        // (ADR 0023), so a name in it stays the name it ran.
        var expected = PublishedImage();
        var named = 0;

        foreach (var (path, text) in Documents().Where(document => !IsTraceReport(document.Path)).Append(("docker-compose.yml", RepositoryPaths.ReadText("docker-compose.yml"))))
        {
            foreach (Match name in ImageName().Matches(text))
            {
                named++;
                Assert.True(
                    name.Value.TrimEnd('.') == expected,
                    FormattableString.Invariant($"{path} names the image {name.Value}, not {expected}."));
            }
        }

        Assert.True(named > 0, "No document names the published image.");
    }

    /// <summary>A workflow's or a job's permissions grant no write to packages, alone or with everything else.</summary>
    private static void AssertGrantsNoPackageWrite(YamlMappingNode node, string where)
    {
        if (!node.Children.TryGetValue(new YamlScalarNode("permissions"), out var permissions))
        {
            return;
        }

        if (permissions is YamlScalarNode scalar)
        {
            Assert.False(scalar.Value == "write-all", FormattableString.Invariant($"{where} grants write-all."));
            return;
        }

        var packages = Scalar(Assert.IsType<YamlMappingNode>(permissions), "packages");
        Assert.True(packages is null or "read" or "none", FormattableString.Invariant($"{where} grants packages: {packages}."));
    }

    /// <summary>What a docker command publishes on the host, refusing every way it would open the host's network otherwise.</summary>
    private static List<string> Published(string[] arguments, string where)
    {
        var bindings = new List<string>();

        for (var index = 0; index < arguments.Length; index++)
        {
            var argument = arguments[index];
            var next = index + 1 < arguments.Length ? arguments[index + 1] : string.Empty;

            Assert.False(
                argument.StartsWith("--publish-all", StringComparison.Ordinal),
                FormattableString.Invariant($"{where} publishes every exposed port."));
            Assert.False(
                argument is "--network=host" or "--net=host" || (argument is "--network" or "--net" && next == "host"),
                FormattableString.Invariant($"{where} runs on the host's network."));

            if (argument == "--publish")
            {
                bindings.Add(next);
            }
            else if (argument.StartsWith("--publish=", StringComparison.Ordinal))
            {
                bindings.Add(argument["--publish=".Length..]);
            }
            else if (argument.Length > 1 && argument[0] == '-' && argument[1] != '-')
            {
                // A cluster of short flags, such as -dp: a flag that takes a value takes the rest of the
                // cluster, or the next argument when nothing is left.
                for (var flag = 1; flag < argument.Length; flag++)
                {
                    var letter = argument[flag];
                    Assert.False(letter == 'P', FormattableString.Invariant($"{where} publishes every exposed port."));

                    if (letter == 'p')
                    {
                        bindings.Add(flag + 1 < argument.Length ? argument[(flag + 1)..] : next);
                        break;
                    }

                    if ("acehlmuvw".Contains(letter, StringComparison.Ordinal))
                    {
                        break;
                    }
                }
            }
        }

        return bindings;
    }

    /// <summary>The arguments of the <c>docker run</c> the <c>image-archive</c> job starts the image with, the image last.</summary>
    private static string[] CiDockerRun()
    {
        var ci = Assert.Single(DockerCommands(
            string.Join('\n', Steps(Jobs(Workflow("ci.yml")).Single(job => job.Name == "image-archive").Job).Select(Run)),
            "\\"));
        Assert.Equal("$REFERENCE", ci[^1]);

        return ci;
    }

    /// <summary>The arguments of each <c>docker run</c> and <c>docker create</c> in a script, its continued lines joined.</summary>
    private static IEnumerable<string[]> DockerCommands(string script, string continuation)
    {
        var joined = script
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace(continuation + "\n", " ", StringComparison.Ordinal);

        foreach (var line in joined.Split('\n'))
        {
            var words = Words(line);

            for (var index = 0; index + 1 < words.Count; index++)
            {
                if (words[index] == "docker" && words[index + 1] is "run" or "create")
                {
                    yield return words.Skip(index + 2).ToArray();
                    break;
                }
            }
        }
    }

    /// <summary>A command line split as bash or PowerShell splits a plain one: at blanks, with quotes removed.</summary>
    private static List<string> Words(string line)
    {
        var words = new List<string>();
        var word = new StringBuilder();
        var quote = '\0';
        var inWord = false;

        foreach (var character in line)
        {
            if (quote != '\0')
            {
                if (character == quote)
                {
                    quote = '\0';
                }
                else
                {
                    word.Append(character);
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                quote = character;
                inWord = true;
            }
            else if (char.IsWhiteSpace(character))
            {
                if (inWord)
                {
                    words.Add(word.ToString());
                    word.Clear();
                    inWord = false;
                }
            }
            else if (character == '#' && !inWord)
            {
                break;
            }
            else
            {
                word.Append(character);
                inWord = true;
            }
        }

        if (inWord)
        {
            words.Add(word.ToString());
        }

        return words;
    }

    private static string Continuation(string language) => language is "powershell" or "pwsh" ? "`" : "\\";

    private static IEnumerable<(string Language, string Body)> CodeBlocks(string text) =>
        CodeBlock().Matches(text.Replace("\r\n", "\n", StringComparison.Ordinal))
            .Select(block => (block.Groups["language"].Value, block.Groups["body"].Value));

    /// <summary>A file inside a trace report's folder — not the reports' index, which is a living document.</summary>
    private static bool IsTraceReport(string relativePath)
    {
        var segments = relativePath.Replace('\\', '/').Split('/');
        return segments.Length > 3 && segments[0] == "docs" && segments[1] == "reports";
    }

    /// <summary>Every document a user or a maintainer reads; not the changelog, which records names as they were.</summary>
    private static IEnumerable<(string Path, string Text)> Documents() =>
        Directory.GetFiles(RepositoryPaths.Root.FullName, "*.md")
            .Concat(Directory.GetFiles(RepositoryPaths.Resolve("docs"), "*.md", SearchOption.AllDirectories))
            .Where(path => Path.GetFileName(path) != "CHANGELOG.md")
            .Order(StringComparer.Ordinal)
            .Select(path => (Path.GetRelativePath(RepositoryPaths.Root.FullName, path), File.ReadAllText(path)));

    /// <summary>The image's name in the registry: the repository's path on GitHub, in lower case.</summary>
    private static string PublishedImage()
    {
        var repository = new Uri(Assert.Single(XDocument.Parse(RepositoryPaths.ReadText("Directory.Build.props")).Descendants("RepositoryUrl")).Value.Trim());
        Assert.Equal("github.com", repository.Host);

        return "ghcr.io/" + repository.AbsolutePath.Trim('/').ToLowerInvariant();
    }

    private static IEnumerable<string> Scalars(YamlNode node) => node switch
    {
        YamlScalarNode scalar => [scalar.Value ?? string.Empty],
        YamlMappingNode mapping => mapping.Children.SelectMany(entry => Scalars(entry.Key).Concat(Scalars(entry.Value))),
        YamlSequenceNode sequence => sequence.Children.SelectMany(Scalars),
        _ => [],
    };

    private static string[] WorkflowFiles() =>
        Directory.GetFiles(RepositoryPaths.Resolve(".github", "workflows"))
            .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
            .Select(path => Path.GetFileName(path)!)
            .Order(StringComparer.Ordinal)
            .ToArray();

    private static YamlMappingNode PublishImage() => Workflow("publish-image.yml");

    private static YamlMappingNode Workflow(string fileName) => Load(RepositoryPaths.ReadText(".github", "workflows", fileName));

    private static YamlMappingNode Compose() => Load(RepositoryPaths.ReadText("docker-compose.yml"));

    private static YamlMappingNode Load(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));

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

    [GeneratedRegex(@"\bdocker (?:image )?push\b")]
    private static partial Regex DockerPush();

    [GeneratedRegex(@"\bdocker build\b")]
    private static partial Regex DockerBuild();

    [GeneratedRegex(@"\n\s*absent\|unreadable\)\n\s*docker push ""\$REFERENCE""\n\s*;;\n")]
    private static partial Regex PushOnAnAnswer();

    [GeneratedRegex(@"```(?<language>[a-z]*)\n(?<body>.*?)```", RegexOptions.Singleline)]
    private static partial Regex CodeBlock();

    [GeneratedRegex(@"ghcr\.io/[a-z0-9][a-z0-9._-]*/[a-z0-9][a-z0-9._-]*", RegexOptions.IgnoreCase)]
    private static partial Regex ImageName();
}
