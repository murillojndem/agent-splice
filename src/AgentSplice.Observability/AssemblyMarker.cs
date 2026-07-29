namespace AgentSplice.Observability;

/// <summary>
/// Stable reference point for architecture tests so that assemblies are loaded deterministically
/// instead of by name lookup. Carries no behaviour.
/// </summary>
public sealed class AssemblyMarker
{
    private AssemblyMarker()
    {
    }
}
