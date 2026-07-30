using AgentSplice.Application.Errors;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
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

    /// <summary>The exchange, or <c>null</c> when the request failed before its model was known.</summary>
    public CompletionExchange? Exchange { get; private init; }

    /// <summary>The ordered observations recorded for this request.</summary>
    public IReadOnlyList<ExchangeObservation> Observations { get; private init; } = [];

    /// <summary>The error reported to the client, or <c>null</c> when none was.</summary>
    public GatewayError? Error { get; private init; }

    /// <summary>Creates a record.</summary>
    public static ExchangeRecord Create(
        ExchangeId exchangeId,
        PublicRequestId requestId,
        IReadOnlyList<ExchangeObservation> observations,
        CompletionExchange? exchange = null,
        GatewayError? gatewayError = null)
    {
        ArgumentNullException.ThrowIfNull(observations);

        return new ExchangeRecord
        {
            ExchangeId = exchangeId,
            RequestId = requestId,
            Exchange = exchange,
            Observations = observations,
            Error = gatewayError,
        };
    }
}
