namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Talks to one family of model runtime (docs/ARCHITECTURE.md "Provider modules implement
/// Application ports").
/// </summary>
/// <remarks>
/// Declares only what the current slice can implement. Completion and streaming arrive with the
/// stages that build them, because a member no caller invokes and no provider implements is a
/// capability the interface claims and the product does not have (CLAUDE.md: no speculative
/// frameworks).
///
/// Implementations never throw a transport exception across this boundary. They classify it into an
/// <see cref="UpstreamFailure"/>, which is what keeps <c>AgentSplice.Application</c> free of
/// <c>System.Net.Http</c>.
/// </remarks>
public interface IModelRuntimeProvider
{
    /// <summary>The <c>provider</c> configuration value this implementation serves, for example <c>lmstudio</c>.</summary>
    string ProviderKey { get; }

    /// <summary>Asks a runtime for its model catalogue.</summary>
    /// <param name="target">The runtime to ask.</param>
    /// <param name="cancellationToken">Cancels the call; the caller's token is always the root.</param>
    Task<RuntimeModelListResult> ListModelsAsync(RuntimeTarget target, CancellationToken cancellationToken);
}
