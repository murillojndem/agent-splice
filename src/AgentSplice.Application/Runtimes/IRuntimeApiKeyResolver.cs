namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Resolves the credential a runtime was configured to use
/// (docs/SPECIFICATION.md FR-DATA-010, docs/SECURITY.md "Upstream credentials").
/// </summary>
/// <remarks>
/// A port rather than a direct environment read so that the secret source is a hosting concern and
/// so the provider can resolve at the moment it builds the request. Only a provider should depend on
/// this: if orchestration held a resolved credential, the value would travel through routing,
/// recording, and observability, and every one of those is a disclosure path.
/// </remarks>
public interface IRuntimeApiKeyResolver
{
    /// <summary>
    /// Returns the runtime's credential, or <see cref="RuntimeCredential.None"/> when none is
    /// configured or the named variable is unset. A missing key is not an error: a local runtime
    /// legitimately needs none.
    /// </summary>
    RuntimeCredential Resolve(RuntimeTarget target);
}
