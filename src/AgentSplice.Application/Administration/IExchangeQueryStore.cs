using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Administration;

/// <summary>
/// Reads retained evidence (FR-DASH-001, FR-TRACE-009).
/// </summary>
/// <remarks>
/// A port so the read path is expressed in views the application owns rather than in whatever the
/// store finds convenient to select. It is deliberately read-only: nothing on this surface edits or
/// deletes evidence, because evidence that an operator can revise is no longer evidence, and
/// removal belongs to the retention policy rather than to a request.
/// </remarks>
public interface IExchangeQueryStore
{
    /// <summary>
    /// Whether this deployment retains exchange metadata at all.
    /// </summary>
    /// <remarks>
    /// Asked before reading rather than inferred from an empty result. FR-DATA-001 makes ephemeral
    /// operation a supported deployment, so "nothing is stored" and "nothing happened" are both true
    /// of it and only one answers the caller's question.
    /// </remarks>
    bool Retains { get; }

    /// <summary>Returns one page, newest first.</summary>
    Task<ExchangePageView> ListAsync(ExchangeQuery query, CancellationToken cancellationToken);

    /// <summary>Returns one exchange, or <c>null</c> when the store does not hold it.</summary>
    Task<ExchangeDetailView?> FindAsync(ExchangeId exchangeId, CancellationToken cancellationToken);

    /// <summary>
    /// Returns an exchange's timeline in sequence order, or <c>null</c> when there is no such
    /// exchange.
    /// </summary>
    /// <remarks>
    /// <c>null</c> and an empty list are different answers: the first says no such exchange, the
    /// second says one exists whose timeline is empty. Collapsing them would make a 404 impossible to
    /// tell from a stored exchange with no boundaries.
    /// </remarks>
    Task<IReadOnlyList<TimelineObservationView>?> FindObservationsAsync(
        ExchangeId exchangeId,
        CancellationToken cancellationToken);
}
