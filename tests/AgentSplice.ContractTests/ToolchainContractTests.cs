using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// One .NET version, named in every place that chooses one: the SDK global.json pins, the target every
/// project builds for, the images the Dockerfile builds and runs on, and the SDK each workflow installs
/// (ADR 0024).
/// </summary>
/// <remarks>
/// Each could move alone and some build would still pass. A workflow left on another SDK need not fail
/// at all: GitHub's runners come with 10.0 SDKs already installed, global.json then picks the newest of
/// those, and the package carries whatever runtime that SDK bundles. An image left on another base
/// builds the gateway for one runtime and starts it on another, which fails only when the container
/// starts; and a project that sets its own target builds for a runtime nobody chose while every other
/// check passes. So the places are read and held to one value on every build.
/// </remarks>
public sealed partial class ToolchainContractTests
{
    [Fact]
    public void Every_place_that_chooses_a_dotnet_version_chooses_the_one_global_json_pins()
    {
        var pinned = PinnedVersion();

        Assert.Equal("net" + pinned, BuildProperty("TargetFramework"));

        var images = BaseImages().ToArray();
        Assert.Equal(["sdk", "aspnet"], images.Select(image => image.Name));
        Assert.All(images, image => Assert.Equal(pinned, image.Version));

        var installed = WorkflowSdkVersions().ToArray();
        Assert.NotEmpty(installed);
        Assert.All(installed, step => Assert.True(
            step.Version == pinned + ".x",
            $"{step.Where} installs dotnet-version '{step.Version}', not '{pinned}.x'."));
    }

    [Fact]
    public void Every_project_builds_for_the_target_and_language_the_build_props_set()
    {
        // The target the test above holds is the one Directory.Build.props sets, so a project file that
        // set its own would build for another runtime with that test still passing. The language is
        // held there too: the runtime moved to .NET 10 and the language stayed C# 12 (ADR 0024).
        Assert.Equal("12.0", BuildProperty("LangVersion"));

        var buildProps = RepositoryPaths.Resolve("Directory.Build.props");
        var overriding = MsBuildFiles()
            .Where(path => !string.Equals(path, buildProps, StringComparison.OrdinalIgnoreCase))
            .Where(path => ToolchainProperty().IsMatch(File.ReadAllText(path)))
            .Select(path => Path.GetRelativePath(RepositoryPaths.Root.FullName, path))
            .ToArray();

        Assert.Empty(overriding);
    }

    [Fact]
    public void The_notices_name_the_image_the_gateway_runs_on()
    {
        // THIRD-PARTY-NOTICES.md describes the operating-system layer the image carries, and that layer
        // is whatever the runtime stage's base image is. A new base must come with new notices.
        var runtime = BaseImages().Last();

        Assert.Contains(
            "`" + runtime.Reference + "`",
            RepositoryPaths.ReadText("THIRD-PARTY-NOTICES.md"),
            StringComparison.Ordinal);
    }

    /// <summary>The major and minor version of the SDK global.json pins, such as <c>10.0</c>.</summary>
    private static string PinnedVersion()
    {
        using var globalJson = JsonDocument.Parse(RepositoryPaths.ReadText("global.json"));

        var sdk = globalJson.RootElement.GetProperty("sdk").GetProperty("version").GetString();
        var match = SdkVersion().Match(sdk ?? string.Empty);

        Assert.True(match.Success, "global.json must pin an SDK version such as 10.0.100.");

        return match.Groups["channel"].Value;
    }

    private static string BuildProperty(string name) =>
        Assert.Single(XDocument.Parse(RepositoryPaths.ReadText("Directory.Build.props")).Descendants(name)).Value.Trim();

    /// <summary>Every Microsoft .NET image the Dockerfile starts a stage from, in order.</summary>
    private static IEnumerable<(string Name, string Version, string Reference)> BaseImages() =>
        DotnetBaseImage()
            .Matches(RepositoryPaths.ReadText("Dockerfile"))
            .Select(match => (match.Groups["name"].Value, match.Groups["version"].Value, match.Groups["reference"].Value));

    /// <summary>
    /// The <c>dotnet-version</c> of every <c>actions/setup-dotnet</c> step in every workflow, with where
    /// it is.
    /// </summary>
    /// <remarks>
    /// Without a <c>dotnet-version</c> a step takes its version from global.json, or installs nothing when
    /// it finds none. Requiring one keeps every SDK a workflow installs in view of this test and of whoever
    /// reads the workflow, so a step without one fails here rather than being skipped.
    /// </remarks>
    private static IEnumerable<(string Where, string Version)> WorkflowSdkVersions()
    {
        var workflows = Directory.GetFiles(RepositoryPaths.Resolve(".github", "workflows"))
            .Where(path => Path.GetExtension(path) is ".yml" or ".yaml")
            .Order(StringComparer.Ordinal);

        foreach (var file in workflows)
        {
            var stream = new YamlStream();
            stream.Load(new StringReader(File.ReadAllText(file)));

            var workflow = (YamlMappingNode)stream.Documents[0].RootNode;

            if (!workflow.Children.TryGetValue(new YamlScalarNode("jobs"), out var jobs))
            {
                continue;
            }

            foreach (var (name, job) in ((YamlMappingNode)jobs).Children)
            {
                if (!((YamlMappingNode)job).Children.TryGetValue(new YamlScalarNode("steps"), out var steps))
                {
                    continue;
                }

                var number = 0;

                foreach (var step in ((YamlSequenceNode)steps).Children.Cast<YamlMappingNode>())
                {
                    number++;

                    var uses = step.Children.TryGetValue(new YamlScalarNode("uses"), out var action)
                        ? ((YamlScalarNode)action).Value ?? string.Empty
                        : string.Empty;

                    if (!uses.StartsWith("actions/setup-dotnet@", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var where = $"{Path.GetFileName(file)}, job {((YamlScalarNode)name).Value}, step {number}";
                    var version = step.Children.TryGetValue(new YamlScalarNode("with"), out var with)
                        && with is YamlMappingNode inputs
                        && inputs.Children.TryGetValue(new YamlScalarNode("dotnet-version"), out var value)
                            ? (value as YamlScalarNode)?.Value
                            : null;

                    Assert.True(version is not null, $"{where} runs actions/setup-dotnet without a dotnet-version.");

                    yield return (where, version);
                }
            }
        }
    }

    /// <summary>Every MSBuild file in the repository, outside build output and installed dependencies.</summary>
    private static IEnumerable<string> MsBuildFiles()
    {
        var pending = new Stack<DirectoryInfo>([RepositoryPaths.Root]);

        while (pending.Count > 0)
        {
            var directory = pending.Pop();

            foreach (var file in directory.EnumerateFiles())
            {
                if (file.Extension is ".csproj" or ".props" or ".targets" or ".pubxml")
                {
                    yield return file.FullName;
                }
            }

            foreach (var child in directory.EnumerateDirectories())
            {
                if (child.Name is not ("bin" or "obj" or "node_modules" or ".git" or ".vs"))
                {
                    pending.Push(child);
                }
            }
        }
    }

    [GeneratedRegex(@"^(?<channel>[0-9]+\.[0-9]+)\.[0-9]+$")]
    private static partial Regex SdkVersion();

    // MSBuild property names are case-insensitive, so the spelling is too.
    [GeneratedRegex(@"<(?:TargetFrameworks?|LangVersion)[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex ToolchainProperty();

    // Flags such as --platform may come between FROM and the image.
    [GeneratedRegex(@"^FROM (?:--\S+\s+)*(?<reference>mcr\.microsoft\.com/dotnet/(?<name>[a-z-]+):(?<version>[0-9]+\.[0-9]+)[^\s]*)", RegexOptions.Multiline)]
    private static partial Regex DotnetBaseImage();
}
