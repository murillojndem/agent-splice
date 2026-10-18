using AgentSplice.Domain.Exchanges;
using Xunit;

namespace AgentSplice.UnitTests.Exchanges;

/// <summary>
/// The one rule that says whether a runtime's answer succeeded (ADR 0026).
/// </summary>
/// <remarks>
/// <c>completed</c> means the transport cycle finished, so a relayed 500 and an error inside a 200 are
/// both completed exchanges with no failure class. This is the rule that tells them from a success,
/// and the metrics, the stored list, and the exchange all use it.
/// </remarks>
public sealed class UpstreamOutcomeRulesTests
{
    [Theory]
    [InlineData(200, false, UpstreamOutcome.Success)]
    [InlineData(204, false, UpstreamOutcome.Success)]
    [InlineData(200, true, UpstreamOutcome.ErrorReported)]
    [InlineData(400, false, UpstreamOutcome.ErrorStatus)]
    [InlineData(429, false, UpstreamOutcome.ErrorStatus)]
    [InlineData(500, false, UpstreamOutcome.ErrorStatus)]
    [InlineData(500, true, UpstreamOutcome.ErrorStatus)]
    public void A_completed_exchange_is_classified_from_its_status_and_whether_it_reported_an_error(
        int status,
        bool reportedError,
        UpstreamOutcome expected)
    {
        // A status outranks the body: a 500 whose envelope is an error object already said it failed.
        Assert.Equal(expected, UpstreamOutcomeRules.Classify(ExchangeStatus.Completed, status, reportedError));
    }

    [Theory]
    [InlineData(ExchangeStatus.Failed)]
    [InlineData(ExchangeStatus.Cancelled)]
    [InlineData(ExchangeStatus.Streaming)]
    public void An_exchange_that_did_not_complete_has_no_outcome(ExchangeStatus status)
    {
        // Its failure class or its cancellation already says how it ended, and the runtime's answer
        // never completed to be judged.
        Assert.Null(UpstreamOutcomeRules.Classify(status, 200, upstreamErrorReported: true));
    }

    [Fact]
    public void An_exchange_that_heard_no_response_has_no_outcome()
    {
        Assert.Null(UpstreamOutcomeRules.Classify(ExchangeStatus.Completed, upstreamStatusCode: null, upstreamErrorReported: false));
    }
}
