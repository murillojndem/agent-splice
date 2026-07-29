namespace AgentSplice.Domain.Measurements;

/// <summary>
/// The unit of a measurement. Bounded on purpose, because units become metric and API dimensions
/// (docs/SPECIFICATION.md FR-OBS-006).
/// </summary>
public enum MeasurementUnit
{
    /// <summary>Elapsed time in milliseconds.</summary>
    Milliseconds = 1,

    /// <summary>A byte count.</summary>
    Bytes = 2,

    /// <summary>A token count.</summary>
    Tokens = 3,

    /// <summary>Tokens per second. Prompt and generation throughput are never combined (FR-OBS-005).</summary>
    TokensPerSecond = 4,

    /// <summary>A dimensionless count, such as a number of SSE events.</summary>
    Count = 5,
}
