using AgentSplice.Application.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Says at startup what a binding beyond loopback exposes (docs/SECURITY.md "Defaults").
/// </summary>
/// <remarks>
/// The administrative token protects <c>/api/v1</c> and nothing else. <c>/v1</c> — completions and
/// model discovery — authenticates no one on any binding, so a gateway a network can reach serves
/// inference to that network: anyone there can run prompts through the configured runtimes and add
/// exchanges to the store. The documents used to promise authentication on such a binding, and an
/// operator who set a token could believe the gateway was closed.
///
/// A warning rather than a refusal. The container image binds every interface inside its container
/// and is published on the host's loopback, and a deployment behind its own authentication is a
/// legitimate one; what must not happen is the exposure going unsaid.
/// </remarks>
internal static class NetworkExposureNotice
{
    /// <summary>Logs the exposure once, when this host listens beyond loopback.</summary>
    internal static void Announce(IConfiguration configuration, ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(logger);

        if (!LoopbackBindingDefault.ListensBeyondLoopback(configuration))
        {
            return;
        }

        logger.LogWarning(
            GatewayEventIds.InferenceReachableBeyondLoopback,
            "This gateway listens beyond loopback, or trusts forwarded headers. /v1 authenticates no one: anyone who can reach it can run completions through the configured runtimes and add exchanges to the store. The administrative token protects /api/v1 only.");
    }
}
