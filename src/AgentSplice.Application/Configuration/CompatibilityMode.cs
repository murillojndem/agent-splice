namespace AgentSplice.Application.Configuration;

/// <summary>
/// What the gateway does with request fields it does not model
/// (docs/SPECIFICATION.md FR-CHAT-004, FR-CHAT-005, docs/API.md "Compatibility policy").
/// </summary>
/// <remarks>
/// The policy is a setting rather than a convention because FR-CHAT-005 requires it to be explicit.
/// A gateway that silently forwards everything and one that silently rejects the unfamiliar are both
/// defensible; being unable to tell which you have is not.
///
/// <c>adapted</c> appears in docs/API.md as a third mode and is deliberately absent here. Adapters
/// are a Stage 4 capability, and declaring a mode nothing implements would offer a policy that
/// cannot be applied.
/// </remarks>
public enum CompatibilityMode
{
    /// <summary>
    /// Forward unmodelled fields verbatim and record their names. The default, because the runtime
    /// is the authority on its own protocol and rejecting a field it would have accepted makes
    /// AgentSplice the source of a failure that does not exist downstream (P-002).
    /// </summary>
    Transparent = 1,

    /// <summary>
    /// Reject a request carrying any top-level field AgentSplice does not model.
    /// </summary>
    /// <remarks>
    /// For deployments that would rather fail loudly than discover months later that a field was
    /// being passed through untouched. It constrains only what the gateway can describe, so a
    /// runtime extension AgentSplice has no concept of is refused rather than forwarded.
    /// </remarks>
    Strict = 2,
}
