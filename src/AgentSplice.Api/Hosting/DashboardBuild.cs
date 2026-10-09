using AgentSplice.Application.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Options;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// The dashboard build this host serves, if it serves one (FR-DASH-002, FR-DASH-007, ADR 0015).
/// </summary>
/// <remarks>
/// Decided when the service is resolved, from validated options, and not while services are
/// registered: the host is still layering configuration then, and a test host adds its overrides
/// afterwards (ADR 0013).
///
/// A directory counts as a build only when it holds <c>index.html</c> <em>and</em> an
/// <c>assets</c> directory. The dashboard's source directory has an <c>index.html</c> too — one
/// that loads <c>/src/main.tsx</c> — and pointing the gateway there by mistake would otherwise serve
/// a page that loads nothing, beside <c>package.json</c> and the rest of the source tree.
/// </remarks>
internal sealed class DashboardBuild : IDisposable
{
    /// <summary>Where Vite writes the hashed files, and where the gateway caches them as immutable.</summary>
    internal const string AssetsDirectoryName = "assets";

    /// <summary>The single page every client route is answered with.</summary>
    internal const string ShellFileName = "index.html";

    /// <summary>Resolves the configured build directory against the content root.</summary>
    public DashboardBuild(IOptions<AgentSpliceOptions> options, IHostEnvironment environment)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);

        var dashboard = options.Value.Dashboard;

        Enabled = dashboard.Enabled;
        Root = Path.GetFullPath(dashboard.RootPath, environment.ContentRootPath);

        // The provider's default exclusions hide dot-prefixed, hidden, and system files, so nothing
        // beginning with a dot is ever served from the build directory.
        if (Enabled && IsBuild(Root))
        {
            Files = new PhysicalFileProvider(Root);
        }
    }

    /// <summary>Whether the operator wants the dashboard served at all.</summary>
    internal bool Enabled { get; }

    /// <summary>The absolute directory the build is looked for in, whether or not one was found.</summary>
    internal string Root { get; }

    /// <summary>The build's files, or <see langword="null"/> when disabled or when no build was found.</summary>
    internal PhysicalFileProvider? Files { get; }

    /// <inheritdoc />
    public void Dispose() => Files?.Dispose();

    private static bool IsBuild(string root) =>
        File.Exists(Path.Combine(root, ShellFileName))
        && Directory.Exists(Path.Combine(root, AssetsDirectoryName));
}
