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
/// A completion's status and headers are committed before its exchange has an outcome — with the
/// runtime's first byte, or with the gateway's envelope — so the correlation headers cannot be applied
/// afterwards. The values travel here; the header names stay in the transport adapter (ADR 0025).
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
