using System.Globalization;

namespace AgentSplice.IntegrationTests.Dashboard;

/// <summary>
/// A stand-in for the output of <c>pnpm build</c> in a unique temporary directory, deleted when the
/// test ends.
/// </summary>
/// <remarks>
/// The tests never depend on a real build: whether one exists on a developer's machine, and what its
/// hashes are, would make them pass or fail by accident. This has the shape the gateway checks for —
/// <c>index.html</c> and an <c>assets</c> directory — plus two things it must never serve: a hidden
/// file inside the build and a file beside it, each holding a sentinel a response body can be searched
/// for.
/// </remarks>
internal sealed class TemporaryDashboardBuild : IDisposable
{
    /// <summary>Appears in the shell's body, and nowhere else.</summary>
    internal const string ShellMarker = "agentsplice-dashboard-fixture-shell";

    /// <summary>A hashed script, as Vite names one.</summary>
    internal const string ScriptPath = "/dashboard/assets/app-3f9a1c.js";

    /// <summary>A hashed stylesheet, as Vite names one.</summary>
    internal const string StylesheetPath = "/dashboard/assets/app-3f9a1c.css";

    /// <summary>The script's content.</summary>
    internal const string ScriptBody = "console.log('agentsplice-dashboard-fixture-script');";

    /// <summary>Held by dot-prefixed files inside the build.</summary>
    internal const string HiddenSentinel = "SENTINEL-DASHBOARD-HIDDEN-FILE";

    /// <summary>Held by a file beside the build directory.</summary>
    internal const string OutsideSentinel = "SENTINEL-DASHBOARD-OUTSIDE-BUILD";

    private readonly string container;

    /// <param name="parent">Where to create it; the system temporary directory when omitted.</param>
    /// <param name="withAssets">False to produce an <c>index.html</c> with no built assets beside it.</param>
    internal TemporaryDashboardBuild(string? parent = null, bool withAssets = true)
    {
        container = Path.Combine(
            parent ?? Path.Combine(Path.GetTempPath(), "agentsplice-tests"),
            "dashboard-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture));

        Root = Path.Combine(container, "dist");
        Directory.CreateDirectory(Root);

        File.WriteAllText(
            Path.Combine(Root, "index.html"),
            $$"""
            <!doctype html>
            <html lang="en">
              <head>
                <meta charset="UTF-8" />
                <title>AgentSplice</title>
                <script type="module" crossorigin src="{{ScriptPath}}"></script>
                <link rel="stylesheet" crossorigin href="{{StylesheetPath}}">
              </head>
              <body><div id="root"></div><!-- {{ShellMarker}} --></body>
            </html>
            """);

        File.WriteAllText(Path.Combine(Root, ".secret"), HiddenSentinel);

        if (withAssets)
        {
            var assets = Path.Combine(Root, "assets");
            Directory.CreateDirectory(assets);

            File.WriteAllText(Path.Combine(assets, "app-3f9a1c.js"), ScriptBody);
            File.WriteAllText(Path.Combine(assets, "app-3f9a1c.css"), "#root { display: block; }");

            // A known content type, so only the hidden-file exclusion stands between it and a client.
            File.WriteAllText(Path.Combine(assets, ".hidden.js"), HiddenSentinel);
        }

        File.WriteAllText(Path.Combine(container, "outside.txt"), OutsideSentinel);
    }

    /// <summary>The build directory, the one <c>agentsplice:dashboard:rootPath</c> names.</summary>
    internal string Root { get; }

    /// <summary>Points a host configuration at this build.</summary>
    internal void ApplyTo(Dictionary<string, string?> settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        settings["agentsplice:dashboard:rootPath"] = Root;
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(container, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary directory is not worth failing a passing test over.
        }
    }
}
