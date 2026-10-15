namespace AgentSplice.Api.Hosting;

/// <summary>
/// Decides the host's content root: the working directory, as ASP.NET Core chooses it, unless this
/// build is the Windows package (ADR 0017).
/// </summary>
/// <remarks>
/// The content root is where <c>appsettings.json</c> is read from and what
/// <c>agentsplice:dashboard:rootPath</c> and a relative SQLite <c>Data Source</c> resolve against.
/// ASP.NET Core takes it from the working directory, which is right for <c>dotnet run</c> — the project
/// directory — and for the container, whose working directory is <c>/app</c>. It is wrong for a
/// published executable started from anywhere else: the gateway finds no settings, so it validates
/// the built-in defaults and refuses to start with an error about a connection string the operator
/// never removed.
///
/// The package therefore says so itself. Its publish profile writes the runtime switch named by
/// <see cref="SwitchName"/> into the executable's runtime configuration, and with it set the content
/// root is the directory the executable is in. No other build carries the switch — not
/// <c>dotnet run</c>, not the container image, not the test host — so for them this returns exactly
/// what <c>WebApplication.CreateBuilder(args)</c> would have used.
///
/// An explicit content root still wins: <c>--contentRoot</c> on the command line, or
/// <c>ASPNETCORE_CONTENTROOT</c> or <c>DOTNET_CONTENTROOT</c> in the environment. It is detected by
/// reading the same sources the host reads it from rather than by parsing arguments here, because a
/// value in <see cref="WebApplicationOptions.ContentRootPath"/> overrides all of them, so passing one
/// whenever the switch is on would silently ignore an operator's choice.
/// </remarks>
internal static class ContentRootDefault
{
    /// <summary>The runtime switch the Windows package's publish profile sets.</summary>
    internal const string SwitchName = "AgentSplice.Hosting.ContentRootIsApplicationDirectory";

    /// <summary>The builder options for this process.</summary>
    internal static WebApplicationOptions For(string[] args) =>
        For(
            args,
            packaged: AppContext.TryGetSwitch(SwitchName, out var enabled) && enabled,
            HostSettings(args),
            AppContext.BaseDirectory);

    /// <summary>The builder options, from what decides them.</summary>
    /// <param name="args">The process arguments, passed through unchanged.</param>
    /// <param name="packaged">Whether this build carries the switch.</param>
    /// <param name="hostSettings">The host settings, as <see cref="HostSettings"/> reads them.</param>
    /// <param name="applicationDirectory">The directory the executable is in.</param>
    internal static WebApplicationOptions For(
        string[] args,
        bool packaged,
        IConfiguration hostSettings,
        string applicationDirectory)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(hostSettings);
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationDirectory);

        return packaged && string.IsNullOrWhiteSpace(hostSettings[HostDefaults.ContentRootKey])
            ? new WebApplicationOptions { Args = args, ContentRootPath = applicationDirectory }
            : new WebApplicationOptions { Args = args };
    }

    /// <summary>
    /// The host settings from the sources a web host reads its content root from: environment
    /// variables prefixed <c>ASPNETCORE_</c> and <c>DOTNET_</c>, then the command line.
    /// </summary>
    internal static IConfiguration HostSettings(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);

        return new ConfigurationBuilder()
            .AddEnvironmentVariables("ASPNETCORE_")
            .AddEnvironmentVariables("DOTNET_")
            .AddCommandLine(args)
            .Build();
    }
}
