namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Talks to one family of model runtime (docs/ARCHITECTURE.md "Provider modules implement
/// Application ports").
/// </summary>
/// <remarks>
/// Declares only what the current slice can implement, because a member no caller invokes and no
/// provider implements is a capability the interface claims and the product does not have
/// (CLAUDE.md: no speculative frameworks).
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

    /// <summary>Forwards a completion and returns whatever the runtime answered.</summary>
    /// <param name="request">The bytes to send and the runtime to send them to.</param>
    /// <param name="cancellationToken">
    /// Cancels the call. The client's token is the root of the provider's own budget chain, so a
    /// disconnect aborts the upstream request rather than merely abandoning the read.
    /// </param>
    Task<ProviderCompletionResult> CompleteAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken);

    /// <summary>Opens a completion for incremental relay and hands back the open body.</summary>
    /// <param name="request">The bytes to send and the runtime to send them to.</param>
    /// <param name="cancellationToken">
    /// Cancels the call and, through the returned stream, the whole relay. The client's token is the
    /// root of the provider's budget chain for the stream's entire life.
    /// </param>
    /// <remarks>
    /// Separate from <see cref="CompleteAsync"/> because the two differ in what they own. The
    /// buffered call ends when it returns; this one hands back a resource whose lifetime — response,
    /// connection, and timeout budgets — outlives the call and ends when
    /// <see cref="ProviderStreamResult.Body"/> is disposed.
    /// </remarks>
    Task<ProviderStreamResult> StreamAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken);
}
