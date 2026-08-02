using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Runtimes;

namespace AgentSplice.Application.Administration;

/// <summary>Build and module information (openapi <c>SystemInfo</c>, FR-HEALTH-005).</summary>
/// <remarks>
/// Redacted by construction rather than by filtering. Every member is a version, a stage name, a
/// module name, or a boolean — there is no path by which a configured value could reach it, so there
/// is nothing here to redact and nothing to get wrong later.
/// </remarks>
public sealed record SystemInfoView
{
    /// <summary>The build's informational version.</summary>
    public required string Version { get; init; }

    /// <summary>The roadmap stage this build implements.</summary>
    public required string Stage { get; init; }

    /// <summary>The modules composed into this host, by name.</summary>
    public required IReadOnlyList<string> EnabledModules { get; init; }

    /// <summary>Whether raw content retention is on. False in every shipped build (FR-DATA-005).</summary>
    public required bool ContentRetentionEnabled { get; init; }

    /// <summary>Whether exchange metadata is retained at all (FR-DATA-001).</summary>
    public required bool MetadataRetentionEnabled { get; init; }
}

/// <summary>One configured runtime with its connection detail redacted (openapi <c>RuntimeSummary</c>).</summary>
/// <remarks>
/// <see cref="ApiKeyEnvironmentVariable"/> is the name of a variable and never its value. The
/// credential is resolved by the provider at the moment it builds an upstream request and never
/// travels through routing, evidence, or this surface (FR-DATA-010).
/// </remarks>
public sealed record RuntimeSummaryView
{
    /// <summary>Stable identifier used in routing, traces, and metric dimensions.</summary>
    public required string RuntimeId { get; init; }

    /// <summary>Provider adapter key.</summary>
    public required string Provider { get; init; }

    /// <summary>Base address of the runtime's OpenAI-compatible surface. Never carries credentials.</summary>
    public required string BaseUrl { get; init; }

    /// <summary>Name of the environment variable holding the key, never the key.</summary>
    public string? ApiKeyEnvironmentVariable { get; init; }

    /// <summary>Whether this runtime participates in routing and discovery.</summary>
    public required bool Enabled { get; init; }

    /// <summary>Whether model discovery is enabled for it.</summary>
    public required bool DiscoveryEnabled { get; init; }
}

/// <summary>One client-visible model with the provenance of what is claimed about it (openapi <c>CatalogModel</c>).</summary>
public sealed record CatalogModelView
{
    /// <summary>The identifier a client sees and sends.</summary>
    public required string ClientModelId { get; init; }

    /// <summary>The runtime that serves it.</summary>
    public required string RuntimeId { get; init; }

    /// <summary>The identifier sent upstream.</summary>
    public required string UpstreamModelId { get; init; }

    /// <summary>The alias that produced it, when one did.</summary>
    public string? AliasId { get; init; }

    /// <summary>How this entry came to exist.</summary>
    public required ModelResolutionSource Source { get; init; }

    /// <summary>
    /// Whether the owning runtime answered the most recent discovery attempt, or <c>null</c> when
    /// there has not been one.
    /// </summary>
    /// <remarks>
    /// Null for a runtime with discovery switched off. Such a runtime stays fully usable through its
    /// aliases, so <c>false</c> would read as unreachable and be wrong; <c>true</c> would be a claim
    /// from no evidence, which is the same defect this surface refuses in runtime health
    /// (FR-TRACE-006, FR-DASH-006).
    /// </remarks>
    public bool? Reachable { get; init; }

    /// <summary>How the capability claims were established.</summary>
    public required CapabilityProvenance CapabilityProvenance { get; init; }

    /// <summary>
    /// Creation time as reported, or <c>null</c> when nothing reported one.
    /// </summary>
    /// <remarks>
    /// Absent here rather than zero. <c>/v1/models</c> emits zero because the OpenAI schema requires a
    /// present integer; that substitution belongs to the compatibility envelope and to nowhere else,
    /// and this is the surface that reports the honest absence (FR-DASH-006).
    /// </remarks>
    public long? Created { get; init; }
}

/// <summary>One runtime's health as the last discovery attempt observed it (openapi <c>RuntimeHealth</c>).</summary>
/// <remarks>
/// <see cref="CheckedAt"/> is <c>null</c> for a runtime nothing has consulted yet, and the status is
/// then <see cref="RuntimeHealthStatus.Unknown"/> rather than healthy. A health surface that reports
/// an unconsulted runtime as healthy is worse than one that reports nothing (FR-TRACE-006).
/// </remarks>
public sealed record RuntimeHealthView
{
    /// <summary>The runtime this describes.</summary>
    public required string RuntimeId { get; init; }

    /// <summary>Health as the last attempt observed it.</summary>
    public required RuntimeHealthStatus Status { get; init; }

    /// <summary>When that attempt happened, or <c>null</c> when there has not been one.</summary>
    public DateTimeOffset? CheckedAt { get; init; }

    /// <summary>True when the catalogue served was past its refresh window.</summary>
    public required bool ServedFromStaleCache { get; init; }
}

/// <summary>Whether the process is up, and whether it is ready to serve (FR-HEALTH-001..003).</summary>
public sealed record ReadinessView
{
    /// <summary>True when the readiness condition this deployment configured is met.</summary>
    public required bool Ready { get; init; }

    /// <summary>Whether readiness requires a reachable runtime here.</summary>
    public required bool RequiresReachableRuntime { get; init; }

    /// <summary>How many enabled runtimes answered their last discovery attempt.</summary>
    public required int ReachableRuntimes { get; init; }

    /// <summary>How many runtimes are enabled.</summary>
    public required int EnabledRuntimes { get; init; }
}
