namespace AgentSplice.Application.Configuration;

/// <summary>
/// Compatibility adapter settings (docs/adr/0006-durable-core-and-adapter-lifecycle.md).
/// </summary>
/// <remarks>
/// Adapters are a Stage 4 concern. The flag exists in Stage 1 so that a profile file which enables
/// adapters fails configuration validation with a clear message instead of being silently ignored:
/// an operator who believes a transformation is active when it is not would misread every trace
/// AgentSplice produces.
/// </remarks>
public sealed class AdapterOptions
{
    /// <summary>Whether the adapter pipeline runs. Must be <c>false</c> in the current stage.</summary>
    public bool Enabled { get; set; }
}
