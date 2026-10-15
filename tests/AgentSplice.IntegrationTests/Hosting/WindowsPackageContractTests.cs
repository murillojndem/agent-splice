using System.Xml.Linq;
using AgentSplice.Api.Hosting;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// The Windows package's publish profile builds what ADR 0017 describes, and nothing package-specific
/// leaks into the builds everything else uses.
/// </summary>
/// <remarks>
/// A file-reading test here rather than in the contract suite because the runtime switch's name is
/// the gateway's own constant, which only this project can see. A profile that drifted from it would
/// publish a package that builds, starts from its own folder, and fails from any other with an error
/// about a missing connection string — which nothing short of the Windows smoke test would notice.
/// And a self-contained or single-file setting moved into the project file would change the container
/// publish and break the test projects that reference the gateway.
/// </remarks>
public sealed class WindowsPackageContractTests
{
    private static readonly string[] PackageOnlyProperties =
        ["RuntimeIdentifier", "RuntimeIdentifiers", "SelfContained", "PublishSingleFile", "PublishTrimmed", "PublishReadyToRun"];

    [Fact]
    public void The_profile_builds_a_self_contained_single_file_win_x64_package()
    {
        var profile = Profile("win-x64.pubxml");

        Assert.Equal("win-x64", Property(profile, "RuntimeIdentifier"));
        Assert.Equal("true", Property(profile, "SelfContained"));
        Assert.Equal("true", Property(profile, "PublishSingleFile"));

        // Not trimmed — ASP.NET Core and EF Core are not trim-safe on .NET 8 — and not compressed,
        // not ReadyToRun, with native libraries beside the executable rather than extracted at start.
        Assert.Equal("false", Property(profile, "PublishTrimmed"));
        Assert.Equal("false", Property(profile, "PublishReadyToRun"));
        Assert.Equal("false", Property(profile, "EnableCompressionInSingleFile"));
        Assert.Equal("false", Property(profile, "IncludeNativeLibrariesForSelfExtract"));

        // A process, not an IIS site: no web.config and no IIS module in the package.
        Assert.Equal("false", Property(profile, "PublishIISAssets"));
    }

    [Fact]
    public void The_profile_makes_the_executables_directory_the_content_root()
    {
        var option = Assert.Single(Items(Profile("win-x64.pubxml"), "RuntimeHostConfigurationOption"));

        Assert.Equal(ContentRootDefault.SwitchName, (string?)option.Attribute("Include"));
        Assert.Equal("true", (string?)option.Attribute("Value"));
    }

    [Fact]
    public void Every_self_contained_profile_carries_the_content_root_switch()
    {
        // A later linux-x64 or win-arm64 profile would otherwise publish a package that finds its
        // settings only when started from its own folder.
        var profiles = Directory.GetFiles(ProfilesDirectory(), "*.pubxml");

        Assert.NotEmpty(profiles);

        foreach (var path in profiles)
        {
            var profile = XDocument.Load(path);

            if (Property(profile, "SelfContained") != "true")
            {
                continue;
            }

            Assert.Contains(
                Items(profile, "RuntimeHostConfigurationOption"),
                option => (string?)option.Attribute("Include") == ContentRootDefault.SwitchName
                    && (string?)option.Attribute("Value") == "true");
        }
    }

    [Fact]
    public void The_package_does_not_ship_the_development_settings()
    {
        var update = Assert.Single(
            Items(Profile("win-x64.pubxml"), "Content"),
            item => (string?)item.Attribute("Update") == "appsettings.Development.json");

        Assert.Equal("Never", (string?)update.Attribute("CopyToPublishDirectory"));
    }

    [Fact]
    public void Nothing_package_specific_is_declared_where_every_build_would_see_it()
    {
        string[][] files =
        [
            ["src", "AgentSplice.Api", "AgentSplice.Api.csproj"],
            ["Directory.Build.props"],
        ];

        foreach (var segments in files)
        {
            var document = XDocument.Parse(RepositoryPaths.ReadText(segments));
            var name = string.Join('/', segments);

            foreach (var property in PackageOnlyProperties)
            {
                Assert.False(
                    document.Descendants(property).Any(),
                    FormattableString.Invariant($"{name} declares {property}; it belongs in the publish profile."));
            }

            Assert.DoesNotContain(
                document.Descendants("RuntimeHostConfigurationOption"),
                option => (string?)option.Attribute("Include") == ContentRootDefault.SwitchName);
        }
    }

    [Fact]
    public void The_package_script_publishes_with_the_profile()
    {
        Assert.Contains(
            "-p:PublishProfile=win-x64",
            RepositoryPaths.ReadText(".github", "scripts", "package-windows.ps1"),
            StringComparison.Ordinal);
    }

    private static string ProfilesDirectory() =>
        RepositoryPaths.Resolve("src", "AgentSplice.Api", "Properties", "PublishProfiles");

    private static XDocument Profile(string fileName) =>
        XDocument.Parse(RepositoryPaths.ReadText("src", "AgentSplice.Api", "Properties", "PublishProfiles", fileName));

    private static string? Property(XDocument profile, string name) =>
        profile.Descendants(name).SingleOrDefault()?.Value.Trim();

    private static IEnumerable<XElement> Items(XDocument profile, string name) =>
        profile.Root!.Elements("ItemGroup").Elements(name);
}
