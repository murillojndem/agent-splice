using AgentSplice.Application.Diagnostics;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Serves the dashboard build at <c>/dashboard/</c> on the gateway's own origin (FR-DASH-002,
/// FR-DASH-007, ADR 0015).
/// </summary>
/// <remarks>
/// Same origin because the dashboard is a client of <c>/api/v1</c> that calls it by relative path:
/// served from here it needs no CORS, no base URL, and no second process, and an alpha installation
/// needs no Node toolchain to open it.
///
/// Everything happens inside one middleware branch under <c>/dashboard</c>, and nothing outside it
/// changes. A fallback endpoint at the root was rejected: one that answers every unmatched GET with
/// the shell turns <c>GET /v1/chat/completions</c> from a 405 into a 200 page, and an unknown
/// <c>/api/v1</c> route from a 404 into HTML the dashboard's own client would parse as an empty
/// result. Within the branch, every client route is answered with the shell, except under
/// <c>/assets</c>, where a file that does not exist is a 404 — never HTML where a script was asked
/// for. Dots are allowed in client routes, because model and runtime identifiers carry them.
///
/// A disabled dashboard maps nothing at all, and a missing build maps nothing and says so once.
/// The gateway starts either way: the dashboard is optional, and a gateway run from source has no
/// build until someone runs <c>pnpm build</c>.
///
/// Responses here carry no correlation identifiers and produce no spans: they are not exchanges, and
/// framework instrumentation is deliberately off (ADR 0014).
/// </remarks>
internal static class DashboardHosting
{
    /// <summary>Where the dashboard is mounted.</summary>
    /// <remarks>
    /// A <see cref="PathString"/> rather than a string so that <c>Map</c> binds to the middleware
    /// branch: with a string, the minimal-API <c>Map(string, Delegate)</c> overload would win and map
    /// an endpoint instead.
    /// </remarks>
    internal static readonly PathString MountPath = new("/dashboard");

    /// <summary>
    /// The shell's canonical URL. The trailing slash matters: the client router's basename carries it,
    /// so <c>/dashboard</c> without one renders nothing there and is redirected here.
    /// </summary>
    internal const string ShellUrl = "/dashboard/";

    /// <summary>The hashed files, relative to the mount.</summary>
    internal static readonly PathString AssetsPath = new("/" + DashboardBuild.AssetsDirectoryName);

    private static readonly PathString ShellPath = new("/" + DashboardBuild.ShellFileName);

    internal static IServiceCollection AddDashboard(this IServiceCollection services)
    {
        services.AddSingleton<DashboardBuild>();

        return services;
    }

    /// <summary>
    /// Mounts the dashboard, when it is enabled and a build exists.
    /// </summary>
    /// <remarks>
    /// Runs after <c>Build</c>, so it reads validated options: a configuration error anywhere in the
    /// tree therefore surfaces here, before the server accepts anything, exactly as it would at host
    /// start.
    /// </remarks>
    internal static WebApplication MapDashboard(this WebApplication app)
    {
        var build = app.Services.GetRequiredService<DashboardBuild>();

        if (!build.Enabled)
        {
            return app;
        }

        var logger = app.Services
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger(typeof(DashboardHosting).FullName!);

        if (build.Files is not { } files)
        {
            logger.LogWarning(
                GatewayEventIds.DashboardBuildMissing,
                "No dashboard build was found at {RootPath}, so nothing is served at {ShellUrl}. A build holds index.html and an assets directory: run 'pnpm build' in web/AgentSplice.Dashboard and point agentsplice:dashboard:rootPath at its dist directory, or set agentsplice:dashboard:enabled to false.",
                build.Root,
                ShellUrl);

            return app;
        }

        // One set of options serves both a requested file and the shell, because Map moves the mount
        // into PathBase: the paths the static file middleware sees are relative to the build.
        var staticFiles = new StaticFileOptions
        {
            FileProvider = files,
            OnPrepareResponse = DashboardResponseHeaders.ApplyCaching,
        };

        // A browser pointed at the gateway lands on the dashboard. Only while one is served: without
        // it the root stays a 404, because a placeholder would let a client mistake an empty gateway
        // for a working one (ADR 0007).
        app.MapMethods("/", [HttpMethods.Get, HttpMethods.Head], RedirectToShell);

        app.Map(MountPath, dashboard =>
        {
            dashboard.Use(ApplySecurityHeaders);
            dashboard.Use(RefuseOtherMethods);
            dashboard.Use(RedirectBareMount);

            // A file that exists is served as itself: index.html requested by name, or a hashed asset.
            dashboard.UseStaticFiles(staticFiles);

            // Anything else is a client route, answered with the shell by the same middleware, so the
            // shell keeps its ETag, conditional requests, and HEAD handling.
            dashboard.Use(ServeShellUnlessAsset);
            dashboard.UseStaticFiles(staticFiles);

            // Explicit, so the branch never falls through into the default terminal, which throws if
            // an endpoint had matched.
            dashboard.Run(NotFound);
        });

        logger.LogInformation(
            GatewayEventIds.DashboardServed,
            "Serving the dashboard at {ShellUrl} from {RootPath}.",
            ShellUrl,
            build.Root);

        return app;
    }

    private static IResult RedirectToShell() => Results.Redirect(ShellUrl);

    /// <summary>Every response under the mount carries the policy, the redirects and refusals included.</summary>
    private static Task ApplySecurityHeaders(HttpContext context, RequestDelegate next)
    {
        DashboardResponseHeaders.ApplySecurity(context.Response.Headers);

        return next(context);
    }

    private static Task RefuseOtherMethods(HttpContext context, RequestDelegate next)
    {
        if (HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method))
        {
            return next(context);
        }

        context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
        context.Response.Headers.Allow = "GET, HEAD";

        return Task.CompletedTask;
    }

    /// <summary>
    /// <c>/dashboard</c> arrives with an empty path inside the branch; <c>/dashboard/</c> arrives as
    /// <c>/</c>. Only the first is redirected, keeping its query.
    /// </summary>
    private static Task RedirectBareMount(HttpContext context, RequestDelegate next)
    {
        if (context.Request.Path.HasValue)
        {
            return next(context);
        }

        context.Response.Redirect(ShellUrl + context.Request.QueryString.Value);

        return Task.CompletedTask;
    }

    private static Task ServeShellUnlessAsset(HttpContext context, RequestDelegate next)
    {
        // The static file middleware has already declined this request. Under /assets that means the
        // file does not exist, and answering with the shell would hand a browser HTML where it asked
        // for a script.
        if (context.Request.Path.StartsWithSegments(AssetsPath, StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;

            return Task.CompletedTask;
        }

        context.Request.Path = ShellPath;

        return next(context);
    }

    private static Task NotFound(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;

        return Task.CompletedTask;
    }
}
