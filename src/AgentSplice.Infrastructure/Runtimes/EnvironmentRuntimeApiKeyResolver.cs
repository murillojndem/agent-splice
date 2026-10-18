using System.Collections.Concurrent;
using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Runtimes;
using Microsoft.Extensions.Logging;

namespace AgentSplice.Infrastructure.Runtimes;

/// <summary>
/// Resolves a runtime credential from the environment variable its configuration names
/// (docs/SPECIFICATION.md FR-DATA-010).
/// </summary>
/// <remarks>
/// A missing or blank variable is not a failure. A local runtime legitimately needs no key, so
/// refusing to start would break the most common deployment. It is worth one warning, though,
/// because "I set <c>apiKeyEnvironmentVariable</c> and the runtime still rejects me" is otherwise
/// diagnosed by guesswork.
///
/// The warning is emitted once per variable name rather than per request: it describes a
/// configuration state, not a request event, and a per-request warning on a busy gateway would bury
/// everything else.
/// </remarks>
public sealed class EnvironmentRuntimeApiKeyResolver : IRuntimeApiKeyResolver
{
    private readonly ConcurrentDictionary<string, bool> warned = new(StringComparer.Ordinal);
    private readonly ILogger<EnvironmentRuntimeApiKeyResolver> logger;

    /// <summary>Creates the resolver.</summary>
    public EnvironmentRuntimeApiKeyResolver(ILogger<EnvironmentRuntimeApiKeyResolver> logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        this.logger = logger;
    }

    /// <inheritdoc />
    public RuntimeCredential Resolve(RuntimeTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        if (target.ApiKeyEnvironmentVariable is not { } variableName)
        {
            return RuntimeCredential.None;
        }

        var credential = RuntimeCredential.FromValue(Environment.GetEnvironmentVariable(variableName));

        if (!credential.HasValue && warned.TryAdd(variableName, true))
        {
            // Names the runtime and the setting, not the setting's value. The setting is where a key
            // gets pasted by mistake, and a key made of letters, digits, and underscores passes as a
            // variable's name; logging the name would then log the key.
            logger.LogWarning(
                GatewayEventIds.RuntimeCredentialMissing,
                "Runtime {RuntimeId} names an environment variable for its API key in its apiKeyEnvironmentVariable setting, but that variable is unset or blank. Requests will be sent without a credential.",
                target.Id.Value);
        }

        return credential;
    }
}
