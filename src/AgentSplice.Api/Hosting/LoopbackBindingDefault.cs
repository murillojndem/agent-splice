using System.Net;
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

    /// <summary>
    /// True when this host will listen somewhere a machine other than this one can reach.
    /// </summary>
    /// <remarks>
    /// Read from the same three keys the default consults, because those are the ones that actually
    /// decide where Kestrel binds. A bare port in HTTP_PORTS means every interface, and so does a
    /// wildcard, <c>0.0.0.0</c>, or <c>[::]</c> in a URL — a container publishing a port produces
    /// exactly that, which is why this has to be a decision an operator makes rather than one a
    /// deployment falls into (FR-HEALTH-006, docs/SECURITY.md).
    ///
    /// Errs towards "reachable" for anything it cannot classify. A wrong answer in that direction
    /// costs a startup failure naming the setting; the other direction costs an unauthenticated
    /// administrative API on a network.
    /// </remarks>
    internal static bool ListensBeyondLoopback(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (ShouldApply(configuration))
        {
            return false;
        }

        // A port list names no host at all, which means every interface.
        if (!string.IsNullOrWhiteSpace(configuration[HttpPortsKey])
            || !string.IsNullOrWhiteSpace(configuration[HttpsPortsKey]))
        {
            return true;
        }

        foreach (var url in (configuration[UrlsKey] ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            if (!IsLoopbackUrl(url.Trim()))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLoopbackUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var parsed))
        {
            // Unparsable, so unclassifiable, so treated as reachable.
            return false;
        }

        return parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6
            ? IPAddress.TryParse(parsed.Host, out var address) && IPAddress.IsLoopback(address)
            : string.Equals(parsed.Host, "localhost", StringComparison.OrdinalIgnoreCase);
    }
}
