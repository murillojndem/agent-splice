namespace AgentSplice.Domain.Measurements;

/// <summary>
/// Where a measured value came from (docs/SPECIFICATION.md section 13.5, FR-OBS-003, FR-OBS-010).
/// </summary>
/// <remarks>
/// Ordered from most to least trustworthy. <see cref="MeasurementProvenanceRules"/> relies on that
/// ordering when a derived value combines several inputs: the value carries the provenance of its
/// weakest input, so an estimate can never pass as a measurement.
///
/// That makes a member a trust rank as well as an origin. A value AgentSplice derived from a count the
/// runtime reported is <see cref="UpstreamReported"/> without the runtime having reported the value
/// itself; generation throughput is the case, and the dashboard says "derived" beside it (ADR 0027).
/// </remarks>
public enum MeasurementProvenance
{
    /// <summary>Observed directly by AgentSplice with its own clock or byte counters.</summary>
    Measured = 1,

    /// <summary>
    /// Reported by the upstream runtime, for example an OpenAI-style <c>usage</c> object, or derived
    /// by AgentSplice from such a report and its own measurements.
    /// </summary>
    UpstreamReported = 2,

    /// <summary>Recovered from an optional runtime log parser (FR-OBS-009).</summary>
    RuntimeLog = 3,

    /// <summary>Reported by the calling client.</summary>
    ClientReported = 4,

    /// <summary>Produced by an AgentSplice estimator, such as a token estimate.</summary>
    Estimated = 5,

    /// <summary>Derived indirectly from other evidence and therefore the weakest claim.</summary>
    Inferred = 6,
}
