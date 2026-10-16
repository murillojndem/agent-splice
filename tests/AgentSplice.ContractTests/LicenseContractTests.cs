using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentSplice.TestSupport;
using Xunit;
using YamlDotNet.RepresentationModel;

namespace AgentSplice.ContractTests;

/// <summary>
/// AgentSplice is licensed under Apache-2.0, every place that says so says the same thing, and every
/// artifact carries the license and the notices of what it bundles (ADR 0020).
/// </summary>
/// <remarks>
/// Nothing here breaks a build or a request, and each failure is public. A license text edited by hand is
/// no longer the license it names. A second file that reads as a license puts GitHub back to reporting
/// "Other". A label, a manifest, and a document that name different licenses or repositories leave a user
/// to guess which one is true. And a dependency added without its notice ships in breach of its own
/// license. So the files are read and held to one another on every build.
/// </remarks>
public sealed partial class LicenseContractTests
{
    /// <summary>
    /// The SHA-256 of https://www.apache.org/licenses/LICENSE-2.0.txt as the Apache Software Foundation
    /// publishes it, LF line endings and all.
    /// </summary>
    private const string ApacheLicenseSha256 = "cfc7749b96f63bd31c3c42b5c471bf756814053e847c10f3eb003417bc523d30";

    private const string Spdx = "Apache-2.0";

    /// <summary>The kinds of restored asset that put a file into the build.</summary>
    private static readonly string[] ShippedAssetGroups = ["runtime", "native", "runtimeTargets", "resource"];

    [Fact]
    public void The_license_is_the_apache_license_word_for_word()
    {
        // .gitattributes checks text out with LF everywhere; the normalisation keeps the digest meaningful
        // for a checkout that ignored it.
        var text = RepositoryPaths.ReadText("LICENSE").Replace("\r\n", "\n", StringComparison.Ordinal);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        Assert.Equal(ApacheLicenseSha256, digest, ignoreCase: true);
    }

    [Fact]
    public void Only_one_file_at_the_root_reads_as_a_license()
    {
        // GitHub's license detection (licensee) reads every root file named like a license, a copyright,
        // or a patents statement, and a REUSE-style LICENSES directory; when two of them disagree it reports
        // the repository's license as "Other".
        var licenses = RepositoryPaths.Root.GetFiles()
            .Select(file => file.Name)
            .Where(name => LicenseFileName().IsMatch(name))
            .ToArray();

        Assert.Equal(["LICENSE"], licenses);
        Assert.False(Directory.Exists(RepositoryPaths.Resolve("LICENSES")), "A LICENSES directory at the root is read as more license files.");
    }

    [Fact]
    public void Every_declaration_names_the_same_license()
    {
        using var package = JsonDocument.Parse(RepositoryPaths.ReadText("web", "AgentSplice.Dashboard", "package.json"));
        Assert.Equal(Spdx, package.RootElement.GetProperty("license").GetString());

        Assert.Equal(Spdx, ImageLabel("org.opencontainers.image.licenses"));

        var license = Mapping(Mapping(OpenApi(), "info"), "license");
        Assert.Equal(Spdx, Scalar(license, "identifier"));
    }

    [Fact]
    public void The_image_names_the_repository_the_build_declares()
    {
        // GitHub links a pushed image to the repository its source label names.
        Assert.Equal(BuildProperty("RepositoryUrl"), ImageLabel("org.opencontainers.image.source"));
    }

    [Fact]
    public void The_notice_names_the_holder_the_build_declares()
    {
        var notice = RepositoryPaths.ReadText("NOTICE").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        Assert.Equal("AgentSplice", notice[0]);
        Assert.Equal(BuildProperty("Copyright"), notice[1]);
    }

    [Fact]
    public void Every_package_the_gateway_restores_is_listed_in_the_third_party_notices()
    {
        var packages = GatewayPackages();

        // Guards on the extraction: the graph is the restored one, transitive packages included, not the
        // handful the projects name — SQLitePCLRaw, the one holder outside Microsoft and OpenTelemetry, is
        // never referenced directly — and only packages that put a file into the build: an analyzer, or
        // the trimming tasks a single-file publish restores, ships nothing.
        Assert.Contains("Microsoft.EntityFrameworkCore.Sqlite.Core", packages);
        Assert.Contains("SQLitePCLRaw.core", packages);
        Assert.Contains("OpenTelemetry.Api", packages);
        Assert.DoesNotContain("Microsoft.EntityFrameworkCore.Analyzers", packages);

        AssertListed(packages, "the gateway's restored dependency graph");
    }

    [Fact]
    public void Every_package_the_dashboard_ships_is_listed_in_the_third_party_notices()
    {
        var packages = DashboardPackages();

        // Guards on the extraction: the production graph, transitive packages included. The build tools are
        // listed in the notices for the code they generate into the bundle, not because the walk finds them.
        Assert.Contains("react", packages);
        Assert.Contains("scheduler", packages);
        Assert.Contains("@remix-run/router", packages);
        Assert.DoesNotContain("vite", packages);

        AssertListed(packages, "the dashboard's production dependency graph");
    }

    [Fact]
    public void The_dashboard_imports_only_from_its_production_dependencies()
    {
        // The bundler takes whatever src/ imports, whichever section of package.json declares it, so the
        // production graph the notices are checked against is sound only if src/ imports nothing else.
        using var package = JsonDocument.Parse(RepositoryPaths.ReadText("web", "AgentSplice.Dashboard", "package.json"));
        var dependencies = package.RootElement.GetProperty("dependencies").EnumerateObject().Select(dependency => dependency.Name).ToHashSet(StringComparer.Ordinal);

        var sources = Directory.GetFiles(RepositoryPaths.Resolve("web", "AgentSplice.Dashboard", "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".ts", StringComparison.Ordinal) || path.EndsWith(".tsx", StringComparison.Ordinal))
            .ToArray();

        Assert.NotEmpty(sources);

        var imported = sources
            .SelectMany(path => ImportSpecifier().Matches(File.ReadAllText(path)).Select(match => match.Groups["specifier"].Value))
            .Where(specifier => !specifier.StartsWith('.') && !specifier.StartsWith('/'))
            .Select(PackageName)
            .ToHashSet(StringComparer.Ordinal);

        // A guard on the extraction.
        Assert.Contains("react", imported);

        foreach (var name in imported)
        {
            Assert.True(
                dependencies.Contains(name),
                FormattableString.Invariant($"The dashboard imports `{name}`, which package.json does not declare under dependencies."));
        }
    }

    [Fact]
    public void The_security_policy_names_the_reporting_channel_of_the_repository_the_build_declares()
    {
        Assert.Contains(
            BuildProperty("RepositoryUrl") + "/security/advisories/new",
            RepositoryPaths.ReadText("SECURITY.md"),
            StringComparison.Ordinal);
    }

    [Fact]
    public void The_image_description_fits_what_the_registry_shows()
    {
        // GitHub's package page shows at most 512 characters of it.
        var description = ImageLabel("org.opencontainers.image.description");

        Assert.InRange(description.Length, 1, 512);
    }

    [Fact]
    public void The_image_carries_the_license_and_the_notices()
    {
        Assert.Matches(NoticeCopy(), RuntimeStage());
    }

    [Fact]
    public void The_package_carries_the_license_and_the_notices_or_is_not_built()
    {
        var script = RepositoryPaths.ReadText(".github", "scripts", "package.ps1");

        Assert.Contains("foreach ($notice in @('LICENSE', 'NOTICE', 'THIRD-PARTY-NOTICES.md'))", script, StringComparison.Ordinal);
        Assert.Contains("every package carries it (ADR 0020)", script, StringComparison.Ordinal);
    }

    [Fact]
    public void A_release_without_the_license_stops_before_anything_is_built()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText(".github", "workflows", "release.yml")));

        var jobs = Mapping((YamlMappingNode)stream.Documents[0].RootNode, "jobs");
        var version = Mapping(jobs, "version");
        var steps = Assert.IsType<YamlSequenceNode>(version.Children[new YamlScalarNode("steps")]).Children.Cast<YamlMappingNode>();
        var check = Assert.Single(steps, step => (Scalar(step, "run") ?? string.Empty).Contains("for notice in", StringComparison.Ordinal));
        var run = Scalar(check, "run")!;

        Assert.Contains("for notice in LICENSE NOTICE THIRD-PARTY-NOTICES.md; do", run, StringComparison.Ordinal);
        Assert.Contains("exit 1", run, StringComparison.Ordinal);

        // Everything that builds runs after the version job.
        Assert.Equal("version", Scalar(Mapping(jobs, "ci"), "needs"));
    }

    /// <summary>
    /// Every package is either in the Packages column of THIRD-PARTY-NOTICES.md's Components table — by its
    /// name, or by a family name ending in <c>.*</c> — or in its list of packages that do not ship, and not
    /// both. A name mentioned anywhere else in the file counts for nothing.
    /// </summary>
    private static void AssertListed(IEnumerable<string> packages, string source)
    {
        var (listed, excluded) = NoticeListings();

        var exact = listed.Where(name => !name.EndsWith(".*", StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal);
        var families = listed.Where(name => name.EndsWith(".*", StringComparison.Ordinal)).Select(name => name[..^1]).ToArray();

        foreach (var package in packages)
        {
            var isListed = exact.Contains(package) || families.Any(family => package.StartsWith(family, StringComparison.Ordinal));

            Assert.True(
                isListed || excluded.Contains(package),
                FormattableString.Invariant($"THIRD-PARTY-NOTICES.md does not list `{package}`, which is in {source}."));
            Assert.False(
                isListed && excluded.Contains(package),
                FormattableString.Invariant($"THIRD-PARTY-NOTICES.md both lists `{package}` and says it does not ship."));
        }
    }

    /// <summary>
    /// The code spans in the Packages column of the Components table, and the package each bullet of
    /// "Packages that do not ship" names first.
    /// </summary>
    private static (string[] Listed, HashSet<string> Excluded) NoticeListings()
    {
        var lines = RepositoryPaths.ReadText("THIRD-PARTY-NOTICES.md").Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        var listed = Section(lines, "## Components")
            .Where(line => line.StartsWith('|') && !line.StartsWith("| Component |", StringComparison.Ordinal) && !line.StartsWith("| ---", StringComparison.Ordinal))
            .SelectMany(row => CodeSpan().Matches(row.Split('|')[2]).Select(match => match.Groups["name"].Value))
            .ToArray();

        var excluded = Section(lines, "## Packages that do not ship")
            .Where(line => line.StartsWith("- `", StringComparison.Ordinal))
            .Select(line => CodeSpan().Match(line).Groups["name"].Value)
            .ToHashSet(StringComparer.Ordinal);

        // A guard on the reading itself: both sections exist and say something.
        Assert.NotEmpty(listed);
        Assert.NotEmpty(excluded);

        return (listed, excluded);
    }

    private static IEnumerable<string> Section(string[] lines, string heading) =>
        lines.SkipWhile(line => line != heading).Skip(1).TakeWhile(line => !line.StartsWith("## ", StringComparison.Ordinal));

    /// <summary>
    /// The packages of the gateway's restored graph, transitive ones included, that put a file into the
    /// build: a runtime, native, or resource asset. An analyzer, or the trimming tasks the SDK restores for
    /// a single-file publish, ships nothing and is left out, so the set is the same whichever restore last
    /// wrote the assets file.
    /// </summary>
    private static HashSet<string> GatewayPackages()
    {
        var path = RepositoryPaths.Resolve("src", "AgentSplice.Api", "obj", "project.assets.json");

        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                FormattableString.Invariant($"'{path}' does not exist; restore the solution before running the contract tests."));
        }

        using var assets = JsonDocument.Parse(File.ReadAllText(path));

        return assets.RootElement.GetProperty("targets").EnumerateObject()
            .SelectMany(target => target.Value.EnumerateObject())
            .Where(entry => entry.Value.GetProperty("type").GetString() == "package" && ShipsAFile(entry.Value))
            .Select(entry => entry.Name[..entry.Name.IndexOf('/', StringComparison.Ordinal)])
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>True when a restored package has an asset that is not NuGet's empty placeholder, <c>_._</c>.</summary>
    private static bool ShipsAFile(JsonElement package) =>
        ShippedAssetGroups.Any(group =>
            package.TryGetProperty(group, out var assets)
            && assets.EnumerateObject().Any(asset => !asset.Name.EndsWith("_._", StringComparison.Ordinal)));

    /// <summary>The package an import specifier names: <c>@scope/name</c> for a scoped one, else its first segment.</summary>
    private static string PackageName(string specifier)
    {
        var segments = specifier.Split('/');

        return specifier.StartsWith('@') && segments.Length > 1 ? segments[0] + "/" + segments[1] : segments[0];
    }

    /// <summary>
    /// The packages of the dashboard's production graph, read from the lockfile: its dependencies, and
    /// theirs, transitively — never its development dependencies.
    /// </summary>
    private static HashSet<string> DashboardPackages()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText("web", "AgentSplice.Dashboard", "pnpm-lock.yaml")));

        var lockfile = (YamlMappingNode)stream.Documents[0].RootNode;
        var snapshots = Mapping(lockfile, "snapshots");
        var pending = new Queue<string>();

        foreach (var (name, dependency) in Entries(Mapping(Mapping(Mapping(lockfile, "importers"), "."), "dependencies")))
        {
            pending.Enqueue(name + "@" + Scalar(Assert.IsType<YamlMappingNode>(dependency), "version"));
        }

        var packages = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);

        while (pending.TryDequeue(out var key))
        {
            if (!visited.Add(key))
            {
                continue;
            }

            // A scoped name starts with '@', so the version separator is the last '@' before any peer suffix.
            var bare = key.Split('(')[0];
            packages.Add(bare[..bare.LastIndexOf('@')]);

            var snapshot = Assert.IsType<YamlMappingNode>(snapshots.Children[new YamlScalarNode(key)]);

            foreach (var group in new[] { "dependencies", "optionalDependencies" })
            {
                if (snapshot.Children.TryGetValue(new YamlScalarNode(group), out var dependencies))
                {
                    foreach (var (name, version) in Entries(Assert.IsType<YamlMappingNode>(dependencies)))
                    {
                        pending.Enqueue(name + "@" + ((YamlScalarNode)version).Value);
                    }
                }
            }
        }

        return packages;
    }

    private static IEnumerable<(string Name, YamlNode Value)> Entries(YamlMappingNode node) =>
        node.Children.Select(entry => (((YamlScalarNode)entry.Key).Value!, entry.Value));

    private static string BuildProperty(string name) =>
        Assert.Single(XDocument.Parse(RepositoryPaths.ReadText("Directory.Build.props")).Descendants(name)).Value.Trim();

    private static string ImageLabel(string name)
    {
        var match = Assert.Single(
            LabelPattern().Matches(RuntimeStage()),
            label => label.Groups["name"].Value == name);

        return match.Groups["value"].Value;
    }

    /// <summary>The Dockerfile's last stage, with continuation lines joined so one instruction is one line.</summary>
    private static string RuntimeStage() =>
        LineContinuation()
            .Replace(RepositoryPaths.ReadText("Dockerfile").Replace("\r\n", "\n", StringComparison.Ordinal), " ")
            .Split("\nFROM ", StringSplitOptions.None)[^1];

    private static YamlMappingNode OpenApi()
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(RepositoryPaths.ReadText("openapi", "agentsplice-openapi.yaml")));

        return (YamlMappingNode)stream.Documents[0].RootNode;
    }

    private static YamlMappingNode Mapping(YamlMappingNode node, string key) =>
        Assert.IsType<YamlMappingNode>(node.Children[new YamlScalarNode(key)]);

    private static string? Scalar(YamlMappingNode node, string key) =>
        node.Children.TryGetValue(new YamlScalarNode(key), out var value) ? (value as YamlScalarNode)?.Value : null;

    /// <summary>
    /// The names GitHub's license detection (licensee's filename patterns) reads as license files: any
    /// mentioning a license, COPYING, or COPYRIGHT, and the OFL and PATENTS files.
    /// </summary>
    [GeneratedRegex(@"licen[cs]e|copying|copyright|^ofl(\.|$)|^patents(\.|$)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LicenseFileName();

    /// <summary>
    /// A module the source pulls in: <c>import … from 'x'</c> other than <c>import type</c>, a bare
    /// <c>import 'x'</c>, <c>export … from 'x'</c>, and <c>import('x')</c>.
    /// </summary>
    [GeneratedRegex(@"(?:^\s*import(?!\s+type\b)[^'""]*?\sfrom\s*|^\s*import\s*|^\s*export\s[^'""]*?\sfrom\s*|\bimport\(\s*)['""](?<specifier>[^'""]+)['""]", RegexOptions.Multiline)]
    private static partial Regex ImportSpecifier();

    [GeneratedRegex(@"(?<name>org\.opencontainers\.image\.[a-z.]+)=""(?<value>[^""]*)""")]
    private static partial Regex LabelPattern();

    [GeneratedRegex(@"`(?<name>[^`\s]+)`")]
    private static partial Regex CodeSpan();

    [GeneratedRegex(@"^COPY LICENSE NOTICE THIRD-PARTY-NOTICES\.md \./\s*$", RegexOptions.Multiline)]
    private static partial Regex NoticeCopy();

    [GeneratedRegex(@"\\[ \t]*\n")]
    private static partial Regex LineContinuation();
}
