namespace AgentSplice.Application.Configuration;

/// <summary>
/// Where exchange metadata is stored (docs/SPECIFICATION.md FR-DATA-001, FR-DATA-002, FR-DATA-003).
/// </summary>
public enum PersistenceMode
{
    /// <summary>No database. AgentSplice runs as a pure ephemeral proxy (FR-DATA-001).</summary>
    None = 0,

    /// <summary>Local SQLite file. The default for a local installation.</summary>
    Sqlite = 1,

    /// <summary>PostgreSQL, for shared or self-hosted installations.</summary>
    Postgres = 2,
}
