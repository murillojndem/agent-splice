using AgentSplice.Application.Errors;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// Everything AgentSplice observed about one completion request.
/// </summary>
/// <remarks>
/// <see cref="Exchange"/> is nullable because a request that fails before its model is known cannot
/// produce one: <see cref="CompletionExchange.Accept"/> requires a valid
/// <see cref="ClientModelId"/>, and inventing a placeholder would fabricate evidence. Such a request
/// still yields a full timeline, a correlation token, and a stable error, so nothing about the
/// client's experience is degraded — but it is not listable as an exchange, which is a gap Stage 1C
/// owns (ADR 0008).
/// </remarks>
public sealed record ExchangeRecord
{
    private ExchangeRecord()
    {
    }

    /// <summary>Identity assigned at ingress, present whether or not an exchange was created.</summary>
    public ExchangeId ExchangeId { get; private init; }

    /// <summary>The correlation token returned to the client.</summary>
    public PublicRequestId RequestId { get; private init; }

    /// <summary>The trace identifier returned to the client, or <c>null</c> when tracing produced none.</summary>
    /// <remarks>
    /// Held here as well as on <see cref="Exchange"/>, because a request refused before its model was
    /// known has no exchange and was still given an <c>x-agentsplice-trace-id</c>. Reading it from the
    /// exchange alone dropped the one link from such a record to its spans.
    /// </remarks>
    public TraceId? TraceId { get; private init; }

    /// <summary>The exchange, or <c>null</c> when the request failed before its model was known.</summary>
    public CompletionExchange? Exchange { get; private init; }

    /// <summary>The ordered observations recorded for this request.</summary>
    public IReadOnlyList<ExchangeObservation> Observations { get; private init; } = [];

    /// <summary>
    /// The values derived from those observations, each carrying its provenance.
    /// </summary>
    /// <remarks>
    /// Separate from the metric stream on purpose. A histogram records a number; a
    /// <see cref="Measurement"/> records a number together with where it came from, which is what
    /// lets a reader tell a clock reading from an upstream-reported token count from an estimate
    /// (P-008, FR-OBS-003, FR-OBS-010). A boundary that was never observed produces no measurement
    /// rather than a zero.
    /// </remarks>
    public IReadOnlyList<Measurement> Measurements { get; private init; } = [];

    /// <summary>The error envelope the client received, or <c>null</c> when none reached it.</summary>
    /// <remarks>
    /// Only an envelope that was written. A response abandoned after it started, and a client that
    /// left, carry none: the exchange's failure class says why it failed, and no code is recorded that
    /// the client never received (ADR 0025).
    /// </remarks>
    public GatewayError? Error { get; private init; }

    /// <summary>Creates a record.</summary>
    public static ExchangeRecord Create(
        ExchangeId exchangeId,
        PublicRequestId requestId,
        IReadOnlyList<ExchangeObservation> observations,
        IReadOnlyList<Measurement>? measurements = null,
        CompletionExchange? exchange = null,
        GatewayError? gatewayError = null,
        TraceId? traceId = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return new ExchangeRecord
        {
            ExchangeId = exchangeId,
            RequestId = requestId,
            TraceId = traceId ?? exchange?.TraceId,
            Exchange = exchange,
            Observations = observations,
            Measurements = measurements ?? [],
            Error = gatewayError,
        };
    }
}
