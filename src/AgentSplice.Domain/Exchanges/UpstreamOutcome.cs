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
    /// <summary>
    /// The runtime answered with a 2xx status, and its answer was read and carried no error.
    /// </summary>
    Success = 1,

    /// <summary>The runtime answered with a status outside 2xx: a redirect, a 4xx, or a 5xx.</summary>
    ErrorStatus = 2,

    /// <summary>
    /// The runtime answered with a 2xx status and reported an error inside its answer, in the
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
    /// The runtime's outcome, or <c>null</c> when there is none to describe or nothing to judge it by.
    /// </summary>
    /// <param name="status">The exchange's status.</param>
    /// <param name="upstreamStatusCode">The runtime's status, when a response was observed.</param>
    /// <param name="upstreamErrorReported">
    /// Whether the runtime's answer carried the error shape, or <c>null</c> when nothing of the answer
    /// was read: no summary was built, or the record predates the fact.
    /// </param>
    /// <remarks>
    /// Defined only for a completed exchange with an observed response. One that failed or was
    /// cancelled already says why through its failure class, and the runtime's answer never
    /// completed to be judged; one with no response heard nothing from the runtime at all.
    ///
    /// Only a 2xx can be a success. A status outside it outranks the body, because it already says the
    /// runtime did not answer the request — a redirect is relayed rather than followed, so a client
    /// receives it as it is. A 2xx whose answer was never read has no outcome rather than
    /// <see cref="UpstreamOutcome.Success"/>: a success is a claim about the answer, and unknown stays
    /// unknown (FR-TRACE-006). That covers an answer the codec could not interpret, a <c>204</c>, and
    /// every row stored before the fact was recorded.
    /// </remarks>
    public static UpstreamOutcome? Classify(ExchangeStatus status, int? upstreamStatusCode, bool? upstreamErrorReported)
    {
        if (status != ExchangeStatus.Completed || upstreamStatusCode is not { } code)
        {
            return null;
        }

        if (code is < 200 or > 299)
        {
            return UpstreamOutcome.ErrorStatus;
        }

        return upstreamErrorReported switch
        {
            true => UpstreamOutcome.ErrorReported,
            false => UpstreamOutcome.Success,
            null => null,
        };
    }
}
