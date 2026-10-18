using Microsoft.AspNetCore.HostFiltering;
using Microsoft.Extensions.Configuration;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Serves only requests addressed to the loopback names, unless <c>AllowedHosts</c> names others
/// (ADR 0027, docs/THREAT_MODEL.md "Browser-originated requests").
/// </summary>
/// <remarks>
/// A web page can point its own domain at <c>127.0.0.1</c> — DNS rebinding — and its requests to the
/// gateway are then same-origin: it can read <c>/v1</c> answers whatever the token, and the packages'
/// <c>/api/v1</c> with no token. Such a request still carries the page's own domain as its
/// <c>Host</c>, so answering only <c>localhost</c>, <c>127.0.0.1</c>, and <c>[::1]</c> is what closes
/// it. ASP.NET Core's host filter does the matching; its default allows every host.
///
/// <c>AllowedHosts</c> is ASP.NET Core's own setting, so a deployment reached by another name — a
/// Compose service name, a LAN address, a reverse proxy — names it there, and <c>*</c> restores the
/// framework's default. A refused request is answered <c>400</c> with no body, before AgentSplice sees
/// it, as an unsupported route is answered <c>404</c>.
/// </remarks>
internal static class LoopbackHostFiltering
{
    /// <summary>The names served when <c>AllowedHosts</c> is not set.</summary>
    internal static readonly IReadOnlyList<string> LoopbackHosts = ["localhost", "127.0.0.1", "[::1]"];

    private const string AllowedHostsKey = "AllowedHosts";

    /// <summary>Applies the loopback names to ASP.NET Core's host filter when nothing else is configured.</summary>
    internal static IServiceCollection AddLoopbackHostFiltering(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // A post-configuration, so it runs after the framework's own, which has already turned an
        // absent setting into "every host". Read from the finished configuration, so a value an
        // environment variable or a test override supplies counts.
        services
            .AddOptions<HostFilteringOptions>()
            .PostConfigure<IConfiguration>((options, configuration) =>
            {
                if (string.IsNullOrWhiteSpace(configuration[AllowedHostsKey]))
                {
                    options.AllowedHosts = [.. LoopbackHosts];
                }

                // The framework's failure page is HTML naming the rejected host; a client gets the
                // status alone.
                options.IncludeFailureMessage = false;
            });

        return services;
    }
}
