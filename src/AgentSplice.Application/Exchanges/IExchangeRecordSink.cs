namespace AgentSplice.Application.Exchanges;

/// <summary>
/// Receives the evidence gathered for a completed request.
/// </summary>
/// <remarks>
/// Not speculative, despite having only a no-op implementation in this stage. It is the only way
/// timeline evidence is observable before persistence and the administrative API exist, so the
/// "routing changes are represented as events" exit criterion would otherwise be untestable — and it
/// is the interface the Stage 1C metadata store implements.
///
/// A sink must never fail an exchange. Persistence failure is recorded as evidence, not surfaced to
/// the client (FR-DATA-009).
/// </remarks>
public interface IExchangeRecordSink
{
    /// <summary>Accepts a completed request's evidence.</summary>
    ValueTask RecordAsync(ExchangeRecord record, CancellationToken cancellationToken);
}
