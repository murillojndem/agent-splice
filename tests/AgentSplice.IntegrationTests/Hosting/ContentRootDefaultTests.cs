using AgentSplice.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentSplice.IntegrationTests.Hosting;

/// <summary>
/// The content root is the working directory everywhere except the Windows package, and an explicit
/// content root wins even there (ADR 0017).
/// </summary>
/// <remarks>
/// The decision is a pure function of the switch, the host settings, and the executable's directory,
/// so it is tested as one: the switch exists only in the package's runtime configuration, and the
/// package itself is proven by the smoke test that starts it from a foreign directory.
/// </remarks>
public sealed class ContentRootDefaultTests
{
    private static readonly string ApplicationDirectory =
        Path.Combine(Path.GetTempPath(), "agentsplice-package") + Path.DirectorySeparatorChar;

    [Fact]
    public void Without_the_switch_the_options_are_the_ones_create_builder_would_use()
    {
        string[] args = ["--urls", "http://127.0.0.1:5999"];

        var options = ContentRootDefault.For(args, packaged: false, ContentRootDefault.HostSettings(args), ApplicationDirectory);

        Assert.Null(options.ContentRootPath);
        Assert.Same(args, options.Args);
    }

    [Fact]
    public void With_the_switch_the_content_root_is_the_executables_directory()
    {
        string[] args = ["--urls", "http://127.0.0.1:5999"];

        var options = ContentRootDefault.For(args, packaged: true, ContentRootDefault.HostSettings(args), ApplicationDirectory);

        Assert.Equal(ApplicationDirectory, options.ContentRootPath);
        Assert.Same(args, options.Args);
    }

    [Theory]
    [InlineData("--contentRoot", "elsewhere")]
    [InlineData("--contentroot=elsewhere")]
    [InlineData("/contentRoot", "elsewhere")]
    [InlineData("contentRoot=elsewhere")]
    public void With_the_switch_a_content_root_on_the_command_line_still_wins(params string[] args)
    {
        // WebApplicationOptions.ContentRootPath overrides every other source, so setting it whenever
        // the switch is on would silently discard what the operator typed.
        var options = ContentRootDefault.For(args, packaged: true, ContentRootDefault.HostSettings(args), ApplicationDirectory);

        Assert.Null(options.ContentRootPath);
    }

    [Fact]
    public void With_the_switch_a_content_root_from_the_environment_still_wins()
    {
        // As the host reads ASPNETCORE_CONTENTROOT or DOTNET_CONTENTROOT: the prefix stripped, the
        // key left. Supplied directly rather than by mutating this process's environment.
        var settings = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>(StringComparer.Ordinal) { ["contentRoot"] = "elsewhere" })
            .Build();

        var options = ContentRootDefault.For([], packaged: true, settings, ApplicationDirectory);

        Assert.Null(options.ContentRootPath);
    }

    [Fact]
    public void The_test_process_does_not_carry_the_switch()
    {
        // The switch is written by the Windows publish profile into the package's runtime
        // configuration alone. Were it present here, every integration test would be running the
        // package's content-root rule instead of the one dotnet run and the image use.
        Assert.False(AppContext.TryGetSwitch(ContentRootDefault.SwitchName, out var enabled) && enabled);
        Assert.Null(ContentRootDefault.For([]).ContentRootPath);
    }
}
