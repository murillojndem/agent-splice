namespace AgentSplice.Application.Configuration;

/// <summary>
/// Metadata persistence settings (docs/SPECIFICATION.md FR-DATA-001 to FR-DATA-003, FR-DATA-009).
/// </summary>
public sealed class PersistenceOptions
{
    /// <summary>Which store is used, if any.</summary>
    public PersistenceMode Mode { get; set; } = PersistenceMode.Sqlite;

    /// <summary>Connection string for the selected store. Required unless the mode is <c>None</c>.</summary>
    public string? ConnectionString { get; set; }

    /// <summary>
    /// Bound on the in-process metadata queue. Streaming must not depend on database latency
    /// (FR-DATA-009, NFR-PERF-004), so the queue is bounded and saturation is reported rather than
    /// allowed to grow without limit.
    /// </summary>
    public int MetadataQueueCapacity { get; set; } = 1024;
}
