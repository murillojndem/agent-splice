namespace AgentSplice.Domain.Runtimes;

/// <summary>
/// How a model capability claim was established (docs/SPECIFICATION.md FR-MOD-007).
/// </summary>
/// <remarks>
/// A capability claim without provenance is how "supports tools" ends up on a model that merely
/// accepted a tools array without honouring it. Stage 1 disables probing by default (FR-MOD-008), so
/// most claims are <see cref="Configured"/> or <see cref="Unknown"/>.
/// </remarks>
public enum CapabilityProvenance
{
    /// <summary>Nothing is known about the capability.</summary>
    Unknown = 1,

    /// <summary>Declared by an operator in configuration.</summary>
    Configured = 2,

    /// <summary>Reported by the runtime's own model catalogue.</summary>
    Discovered = 3,

    /// <summary>Established by an explicit AgentSplice probe request.</summary>
    Probed = 4,

    /// <summary>Derived from indirect evidence such as a model family naming convention.</summary>
    Inferred = 5,
}
