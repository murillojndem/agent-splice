namespace AgentSplice.Application.Configuration;

/// <summary>
/// Liveness and readiness policy (docs/SPECIFICATION.md FR-HEALTH-001 to FR-HEALTH-003).
/// </summary>
public sealed class HealthOptions
{
    /// <summary>
    /// Whether readiness requires at least one enabled runtime to have answered.
    /// </summary>
    /// <remarks>
    /// Off by default, and the default is the interesting part. A gateway whose runtime is down is
    /// still correctly configured and is still the component able to report the outage — reporting
    /// itself unready would make an orchestrator restart or remove it, replacing a diagnosable
    /// runtime failure with an undiagnosable gateway one.
    ///
    /// Deployments that front AgentSplice with a load balancer and want it out of rotation when it
    /// cannot serve turn this on deliberately (FR-HEALTH-003).
    /// </remarks>
    public bool RequireReachableRuntime { get; set; }
}
