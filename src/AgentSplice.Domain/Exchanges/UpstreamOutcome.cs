namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// What the runtime's own answer said about how it went, for an exchange whose transport cycle
/// completed (ADR 0026).
/// </summary>
/// <remarks>
/// The answer to "did the runtime succeed", kept apart from <see cref="ExchangeStatus"/>, which says
/// only that AgentSplice forwarded a request and relayed an answer (ADR 0008 decision 12). A relayed
/// <c>500</c> and an error event inside a <c>200</c> stream are both completed exchanges with no
/// failure class, because AgentSplice did not fail; this is what tells them from a success.
/// </remarks>
public enum UpstreamOutcome
{
    /// <summary>The runtime answered with a success status and reported no error in its answer.</summary>
    Success = 1,

    /// <summary>The runtime answered with a 4xx or 5xx status.</summary>
    ErrorStatus = 2,

    /// <summary>
    /// The runtime answered with a success status and reported an error inside its answer, in the
    /// protocol's own error shape.
    /// </summary>
    ErrorReported = 3,
}

/// <summary>The one rule that classifies a runtime's answer.</summary>
/// <remarks>
/// Shared by the exchange, the metrics, and the stored list, so a runtime's failure cannot be counted
/// one way and listed another.
/// </remarks>
public static class UpstreamOutcomeRules
{
    /// <summary>
    /// The runtime's outcome, or <c>null</c> when there is none to describe.
    /// </summary>
    /// <remarks>
    /// Defined only for a completed exchange with an observed response. One that failed or was
    /// cancelled already says why through its failure class, and the runtime's answer never
    /// completed to be judged; one with no response heard nothing from the runtime at all. A status
    /// outranks an error reported inside the body, because a 4xx or 5xx already says the runtime
    /// failed whatever its body holds.
    /// </remarks>
    public static UpstreamOutcome? Classify(ExchangeStatus status, int? upstreamStatusCode, bool upstreamErrorReported)
    {
        if (status != ExchangeStatus.Completed || upstreamStatusCode is not { } code)
        {
            return null;
        }

        if (code >= 400)
        {
            return UpstreamOutcome.ErrorStatus;
        }

        return upstreamErrorReported ? UpstreamOutcome.ErrorReported : UpstreamOutcome.Success;
    }
}
