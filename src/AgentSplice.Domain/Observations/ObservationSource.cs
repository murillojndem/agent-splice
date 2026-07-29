namespace AgentSplice.Domain.Observations;

/// <summary>
/// Where the evidence for an observation came from (docs/SPECIFICATION.md section 13.4).
/// </summary>
public enum ObservationSource
{
    /// <summary>Observed directly by AgentSplice on the request path.</summary>
    Gateway = 1,

    /// <summary>Reported by the calling client.</summary>
    Client = 2,

    /// <summary>Reported by the upstream runtime in its protocol response.</summary>
    Upstream = 3,

    /// <summary>Recovered from an optional runtime log parser (FR-OBS-009).</summary>
    RuntimeLog = 4,
}
