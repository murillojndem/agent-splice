using Microsoft.AspNetCore.StaticFiles;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// The headers every dashboard response carries (FR-DASH-008, docs/THREAT_MODEL.md "Dashboard XSS",
/// ADR 0015).
/// </summary>
/// <remarks>
/// The dashboard renders values that clients and runtimes chose — model identifiers, runtime names,
/// error messages — on the same origin as the administrative API, while holding the operator's bearer
/// token in memory. React's text rendering is the first defence; this policy is the second, so that a
/// rendering mistake cannot become script that reads the token or the evidence.
///
/// The policy admits exactly what the production build uses: one same-origin module script, one
/// same-origin stylesheet, and same-origin <c>fetch</c>. Styles React sets through the CSSOM — the
/// waterfall's bar widths — are not inline styles in the policy's sense and need no exception. A
/// CSS-in-JS library that injects <c>&lt;style&gt;</c> elements would need a nonce, which is why none is
/// used. <c>upgrade-insecure-requests</c> is absent on purpose: the gateway is plain HTTP on loopback.
///
/// Framing is refused outright. The cost is that an editor's embedded browser that renders pages in an
/// iframe cannot show the dashboard; the benefit is that no other page can overlay it (ADR 0015).
/// </remarks>
internal static class DashboardResponseHeaders
{
    /// <summary>The content security policy, in full.</summary>
    internal const string ContentSecurityPolicy =
        "default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:; font-src 'self'; "
        + "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    /// <summary>For files whose names carry a content hash: a changed file is a different URL.</summary>
    internal const string ImmutableCaching = "public, max-age=31536000, immutable";

    /// <summary>For the shell, which must be revalidated so a new build is picked up on the next load.</summary>
    internal const string Revalidate = "no-cache";

    /// <summary>Applies the security headers. Called before anything else answers under the mount.</summary>
    internal static void ApplySecurity(IHeaderDictionary headers)
    {
        ArgumentNullException.ThrowIfNull(headers);

        headers.ContentSecurityPolicy = ContentSecurityPolicy;
        headers.XContentTypeOptions = "nosniff";
        headers.XFrameOptions = "DENY";

        // IHeaderDictionary has no typed property for this one.
        headers["Referrer-Policy"] = "no-referrer";
    }

    /// <summary>Chooses the caching policy for a file the static file middleware is about to send.</summary>
    internal static void ApplyCaching(StaticFileResponseContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var hashed = context.Context.Request.Path.StartsWithSegments(
            DashboardHosting.AssetsPath,
            StringComparison.OrdinalIgnoreCase);

        context.Context.Response.Headers.CacheControl = hashed ? ImmutableCaching : Revalidate;
    }
}
