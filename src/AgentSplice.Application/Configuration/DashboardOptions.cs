namespace AgentSplice.Application.Configuration;

/// <summary>
/// Whether the gateway serves the built dashboard, and from where (FR-DASH-002, FR-DASH-007,
/// ADR 0015).
/// </summary>
/// <remarks>
/// The dashboard is optional: a gateway with no build beside it still starts and serves every API,
/// and says once at startup that there is nothing to show at <c>/dashboard/</c>. The switch exists
/// for the opposite case — a deployment that ships the build, as the container image does, but does
/// not want a browser-facing surface at all.
/// </remarks>
public sealed class DashboardOptions
{
    /// <summary>Whether a build found at <see cref="RootPath"/> is served. On by default.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>
    /// The directory holding the dashboard build — the output of <c>pnpm build</c>, with
    /// <c>index.html</c> and an <c>assets</c> directory.
    /// </summary>
    /// <remarks>
    /// Resolved against the content root when relative, as ASP.NET Core resolves its web root, so the
    /// default names <c>/app/dashboard</c> in the container image. The operator chooses this
    /// directory and the gateway trusts the choice: every file beneath it with a known content type
    /// is served, so it should hold the build and nothing else.
    /// </remarks>
    public string RootPath { get; set; } = "dashboard";
}
