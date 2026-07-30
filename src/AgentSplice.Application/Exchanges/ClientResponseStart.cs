using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Exchanges;

/// <summary>Whether a write reached the client.</summary>
public enum ClientWriteResult
{
    /// <summary>The bytes were written and flushed.</summary>
    Written = 1,

    /// <summary>The client is gone; nothing further can be written.</summary>
    ClientGone = 2,
}

/// <summary>Everything committed at the moment a response starts.</summary>
/// <remarks>
/// A streamed response commits its status and headers before the exchange has an outcome, so the
/// correlation headers can no longer be applied afterwards the way the buffered path applies them.
/// The values travel here; the header names stay in the transport adapter, which is the same split
/// the buffered path already uses.
/// </remarks>
public sealed record ClientResponseStart(
    int StatusCode,
    string MediaType,
    GatewayCorrelation Correlation,
    IReadOnlyDictionary<string, string> RelayedHeaders,
    bool DisableCaching);

/// <summary>The correlation values a response is entitled to carry.</summary>
public sealed record GatewayCorrelation(
    PublicRequestId RequestId,
    ExchangeId? ExchangeId,
    TraceId? TraceId,
    RuntimeEndpointId? Runtime);
