namespace AgentSplice.Protocols.OpenAI;

/// <summary>
/// Stable reference point for architecture tests so that assemblies are loaded deterministically
/// instead of by name lookup. Carries no behaviour.
/// </summary>
/// <remarks>
/// This project is intentionally empty in Stage 0. The ingress DTOs, incremental SSE reader and
/// writer, and structural summariser arrive with Stage 1A and 1B; creating them now would be a
/// speculative framework, which CLAUDE.md forbids.
/// </remarks>
public sealed class AssemblyMarker
{
    private AssemblyMarker()
    {
    }
}
