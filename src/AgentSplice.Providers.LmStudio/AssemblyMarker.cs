namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Stable reference point for architecture tests so that assemblies are loaded deterministically
/// instead of by name lookup. Carries no behaviour.
/// </summary>
/// <remarks>
/// This project is intentionally empty in Stage 0. The provider adapter arrives with Stage 1A. Its
/// existence now is what lets the architecture tests assert that no vendor-specific type has leaked
/// into the durable core.
/// </remarks>
public sealed class AssemblyMarker
{
    private AssemblyMarker()
    {
    }
}
