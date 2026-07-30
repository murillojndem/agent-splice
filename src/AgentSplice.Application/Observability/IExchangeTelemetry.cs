using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;

namespace AgentSplice.Application.Observability;

/// <summary>
/// Instruments the request path (docs/SPECIFICATION.md FR-OBS-001, FR-OBS-006).
/// </summary>
/// <remarks>
/// A port so that the application decides <em>what</em> is worth recording while the observability
/// module decides how, and so the request path can be tested without an activity listener or a
/// metric collector attached.
/// </remarks>
public interface IExchangeTelemetry
{
    /// <summary>Starts the span covering one completion exchange.</summary>
    IExchangeTrace StartExchange();

    /// <summary>Starts the span covering one upstream provider call.</summary>
    IDisposable? StartProviderRequest(RuntimeEndpointId runtime, string providerKey);

    /// <summary>Starts the span covering the relay of one streamed response.</summary>
    /// <remarks>
    /// Distinct from the provider span, which ends when the response is opened. This one covers the
    /// interval a streamed exchange actually spends transferring, which for a long generation is
    /// nearly the whole exchange and is invisible in the other two spans.
    /// </remarks>
    IDisposable? StartStream(RuntimeEndpointId runtime, string providerKey);

    /// <summary>Records the outcome of a finished exchange.</summary>
    void RecordExchange(ExchangeTelemetrySnapshot snapshot);

    /// <summary>Records how long a model discovery refresh took.</summary>
    void RecordDiscovery(RuntimeEndpointId runtime, TimeSpan duration);
}

/// <summary>The span covering one exchange.</summary>
public interface IExchangeTrace : IDisposable
{
    /// <summary>The trace identifier, or <c>null</c> when no activity exists.</summary>
    /// <remarks>
    /// AgentSplice never invents one. When tracing produced no activity the value is absent, which
    /// is what FR-TRACE-006 requires of missing evidence.
    /// </remarks>
    TraceId? TraceId { get; }

    /// <summary>Records the runtime once routing has chosen one.</summary>
    void SetRuntime(RuntimeEndpointId runtime, string providerKey);

    /// <summary>Records how the exchange ended.</summary>
    void SetOutcome(ExchangeStatus status, string? errorType);
}

/// <summary>
/// What a finished exchange contributes to metrics.
/// </summary>
/// <remarks>
/// <paramref name="UpstreamStatusClass"/> rather than a status code, and no model identifier at all:
/// dimensions must have small closed value sets, and a client-supplied model name would let one
/// caller multiply the cardinality of every series without limit (FR-OBS-006).
///
/// Every streaming member is nullable and every one is absent for a buffered exchange. A zero would
/// claim the exchange streamed and produced nothing, which is a different statement from "this was
/// never a stream" (FR-OBS-004).
/// </remarks>
public sealed record ExchangeTelemetrySnapshot(
    IngressProtocol Protocol,
    RuntimeEndpointId? Runtime,
    string? ProviderKey,
    bool Streaming,
    ExchangeStatus Status,
    string? ErrorType,
    string? UpstreamStatusClass,
    TimeSpan TotalDuration,
    TimeSpan? UpstreamDuration,
    TimeSpan? TimeToHeaders,
    TokenCount? PromptTokens,
    TokenCount? CompletionTokens)
{
    /// <summary>How the stream ended, or <c>null</c> when the exchange never streamed.</summary>
    public StreamTermination? StreamTermination { get; init; }

    /// <summary>Time from opening the upstream request to its first body byte.</summary>
    public TimeSpan? TimeToFirstByte { get; init; }

    /// <summary>Time from accepting the request to the first event carrying model output.</summary>
    public TimeSpan? TimeToFirstSemanticEvent { get; init; }

    /// <summary>Time from accepting the request to the first whole event flushed to the client.</summary>
    public TimeSpan? TimeToFirstClientEvent { get; init; }

    /// <summary>Events delivered to the client, or <c>null</c> when the exchange never streamed.</summary>
    public int? StreamEvents { get; init; }

    /// <summary>Bytes forwarded to the client, or <c>null</c> when the exchange never streamed.</summary>
    public long? StreamBytes { get; init; }

    /// <summary>
    /// Generation throughput, with the provenance of the token count it was derived from.
    /// </summary>
    /// <remarks>
    /// There is deliberately no prompt-throughput counterpart. Nothing observable marks the end of
    /// prompt processing, so the only interval available measures the prompt, the queue, and the
    /// network together (FR-OBS-005).
    /// </remarks>
    public Measurement? GenerationThroughput { get; init; }
}
