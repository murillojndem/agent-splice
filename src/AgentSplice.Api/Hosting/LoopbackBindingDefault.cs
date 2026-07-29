using Microsoft.Extensions.Configuration;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Decides whether the host should fall back to a loopback-only binding.
/// </summary>
/// <remarks>
/// AgentSplice traffic carries prompts and model output, so an unconfigured process must not be
/// reachable from the local network (docs/SPECIFICATION.md NFR 14.3, docs/SECURITY.md).
///
/// The default is applied as a fallback rather than declared in <c>appsettings.json</c>, and the
/// distinction is not cosmetic. <c>WebApplicationBuilder</c> layers <c>appsettings.json</c> over the
/// host configuration that carries <c>ASPNETCORE_URLS</c>, so a <c>Urls</c> value in the settings file
/// silently wins over the environment variable. In a container that produces a process bound to
/// loopback inside the container, where the published port mapping cannot reach it, and the only
/// symptom is a connection refused from outside.
/// </remarks>
internal static class LoopbackBindingDefault
{
    /// <summary>The loopback address and port AgentSplice binds when nothing else is configured.</summary>
    internal const string Urls = "http://127.0.0.1:5280";

    private const string UrlsKey = "urls";
    private const string HttpPortsKey = "HTTP_PORTS";
    private const string HttpsPortsKey = "HTTPS_PORTS";

    /// <summary>
    /// True when no binding has been configured by any means, so the loopback default applies.
    /// </summary>
    internal static bool ShouldApply(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        return string.IsNullOrWhiteSpace(configuration[UrlsKey])
            && string.IsNullOrWhiteSpace(configuration[HttpPortsKey])
            && string.IsNullOrWhiteSpace(configuration[HttpsPortsKey]);
    }
}
