using System.Text.RegularExpressions;
using System.Xml.Linq;
using AgentSplice.Api.Hosting;
using AgentSplice.TestSupport;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// Each self-contained package's publish profile builds what ADR 0017 and ADR 0021 describe, the package
/// script builds exactly those packages, and nothing package-specific leaks into the builds everything
/// else uses.
/// </summary>
/// <remarks>
/// A file-reading test here rather than in the contract suite because the runtime switch's name is
/// the gateway's own constant, which only this project can see. A profile that drifted from it would
/// publish a package that builds, starts from its own folder, and fails from any other with an error
/// about a missing connection string — which nothing short of the smoke test would notice. And a
/// self-contained or single-file setting moved into the project file would change the container
/// publish and break the test projects that reference the gateway.
/// </remarks>
public sealed partial class SelfContainedPackageContractTests
{
    private static readonly string[] PackageOnlyProperties =
        ["RuntimeIdentifier", "RuntimeIdentifiers", "SelfContained", "PublishSingleFile", "PublishTrimmed", "PublishReadyToRun"];

    /// <summary>The platforms a package is built for, one publish profile each.</summary>
    private static readonly string[] PlatformNames = ["linux-x64", "win-x64"];

    public static TheoryData<string> Platforms()
    {
        var platforms = new TheoryData<string>();

        foreach (var platform in PlatformNames)
        {
            platforms.Add(platform);
        }

        return platforms;
    }

    [Fact]
    public void The_self_contained_profiles_are_the_platforms_a_package_is_built_for()
    {
        // A deleted or a new profile must change the platform list, the package script, and CI together.
        var selfContained = Directory.GetFiles(ProfilesDirectory(), "*.pubxml")
            .Where(path => Property(XDocument.Load(path), "SelfContained") == "true")
            .Select(path => Path.GetFileNameWithoutExtension(path)!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(PlatformNames.Order(StringComparer.Ordinal), selfContained);
    }

    [Theory]
    [MemberData(nameof(Platforms))]
    public void The_profile_builds_a_self_contained_single_file_package(string platform)
    {
        var profile = Profile(platform);

        Assert.Equal(platform, Property(profile, "RuntimeIdentifier"));
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

    [Theory]
    [MemberData(nameof(Platforms))]
    public void The_profile_makes_the_executables_directory_the_content_root(string platform)
    {
        var option = Assert.Single(Items(Profile(platform), "RuntimeHostConfigurationOption"));

        Assert.Equal(ContentRootDefault.SwitchName, (string?)option.Attribute("Include"));
        Assert.Equal("true", (string?)option.Attribute("Value"));
    }

    [Fact]
    public void Every_self_contained_profile_carries_the_content_root_switch()
    {
        // A later win-arm64 or linux-arm64 profile would otherwise publish a package that finds its
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

    [Theory]
    [MemberData(nameof(Platforms))]
    public void The_package_does_not_ship_the_development_settings(string platform)
    {
        var update = Assert.Single(
            Items(Profile(platform), "Content"),
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
    public void The_package_script_builds_each_platform_with_its_own_profile()
    {
        var script = RepositoryPaths.ReadText(".github", "scripts", "package.ps1");
        var accepted = ValidateSet().Match(script);

        Assert.True(accepted.Success, "package.ps1 must declare the platforms it builds in a ValidateSet.");
        Assert.Equal(
            PlatformNames.Order(StringComparer.Ordinal),
            accepted.Groups["platforms"].Value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(platform => platform.Trim('\''))
                .Order(StringComparer.Ordinal));

        // The publish itself takes the profile named for the platform it was given, and no profile is named
        // anywhere else by a literal that would build one platform's package for another.
        Assert.Matches(PublishWithTheGivenProfile(), script);
        Assert.DoesNotMatch(LiteralProfile(), script);
    }

    private static string ProfilesDirectory() =>
        RepositoryPaths.Resolve("src", "AgentSplice.Api", "Properties", "PublishProfiles");

    private static XDocument Profile(string platform) =>
        XDocument.Parse(RepositoryPaths.ReadText("src", "AgentSplice.Api", "Properties", "PublishProfiles", platform + ".pubxml"));

    private static string? Property(XDocument profile, string name) =>
        profile.Descendants(name).SingleOrDefault()?.Value.Trim();

    private static IEnumerable<XElement> Items(XDocument profile, string name) =>
        profile.Root!.Elements("ItemGroup").Elements(name);

    [GeneratedRegex(@"\[ValidateSet\((?<platforms>[^)]*)\)\]")]
    private static partial Regex ValidateSet();

    [GeneratedRegex(@"Invoke-Native dotnet @\(\s*'publish',[^)]*""-p:PublishProfile=\$RuntimeIdentifier""")]
    private static partial Regex PublishWithTheGivenProfile();

    [GeneratedRegex(@"PublishProfile=(?!\$RuntimeIdentifier)[A-Za-z]")]
    private static partial Regex LiteralProfile();
}
