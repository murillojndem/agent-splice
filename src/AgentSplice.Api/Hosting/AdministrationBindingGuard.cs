using AgentSplice.Application.Configuration;
using Microsoft.Extensions.Configuration;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Refuses to start a network-reachable gateway whose administrative API has no credential
/// (FR-HEALTH-006, docs/SECURITY.md).
/// </summary>
/// <remarks>
/// The per-request filter is the authority on any individual call — a binding is what an operator
/// configured, a remote address is what actually arrived — but a check that only runs per request
/// fails open in the case that matters: nobody notices until someone reads the traces from another
/// machine.
///
/// Startup is where this belongs for the same reason every other configuration rule lives there.
/// NFR 14.2 puts configuration validation before readiness, and "your administrative API is exposed"
/// is not a fact worth discovering from an access log.
///
/// It refuses rather than warns. A warning in a startup log is a message an operator reads once, if
/// the log level happens to be right, on the deployment least likely to be watched.
/// </remarks>
internal static class AdministrationBindingGuard
{
    /// <summary>Throws when this host would listen beyond loopback with no administrative token.</summary>
    /// <exception cref="InvalidOperationException">The combination is refused.</exception>
    internal static void Verify(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        if (!LoopbackBindingDefault.ListensBeyondLoopback(configuration))
        {
            return;
        }

        var variable = configuration[
            $"{AgentSpliceOptions.SectionName}:administration:apiKeyEnvironmentVariable"];

        if (!string.IsNullOrWhiteSpace(variable)
            && !string.IsNullOrEmpty(Environment.GetEnvironmentVariable(variable)))
        {
            return;
        }

        // Names the setting and the variable, never a value. An operator needs to know which knob to
        // turn; nothing here needs to say what is currently in it.
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(variable)
                ? "This host is configured to listen beyond loopback, and the administrative API has no credential. Set agentsplice:administration:apiKeyEnvironmentVariable to the name of an environment variable holding a bearer token, or bind to loopback only."
                : FormattableString.Invariant(
                    $"This host is configured to listen beyond loopback, and the environment variable '{variable}' named by agentsplice:administration:apiKeyEnvironmentVariable is unset or empty."));
    }
}
