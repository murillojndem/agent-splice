namespace AgentSplice.Infrastructure.Persistence.Rows;

/// <summary>
/// One persisted completion exchange (docs/SPECIFICATION.md section 13.3).
/// </summary>
/// <remarks>
/// Deliberately not <see cref="Domain.Exchanges.CompletionExchange"/>. The domain record is immutable
/// with private <c>init</c> setters and value-object identifiers, and mapping it directly would put
/// EF Core conventions — a persistence framework — inside <c>AgentSplice.Domain</c>, which
/// docs/ARCHITECTURE.md forbids and an architecture test enforces.
///
/// Separating the two also lets the store record something the domain refuses to construct. A request
/// that named an unknown model fails before <see cref="Domain.Exchanges.CompletionExchange.Accept"/>
/// can run, because that method requires a valid model identifier and inventing a placeholder would
/// fabricate evidence (ADR 0008) — yet a client sending a model the gateway does not know is the most
/// common misconfiguration there is, and the one an operator most needs to see. Here
/// <see cref="ClientModelId"/> and <see cref="Streaming"/> are nullable, so the request is listable
/// without claiming a model it never named.
///
/// Timestamps are UTC ticks. Every AgentSplice timestamp comes from <c>TimeProvider.GetUtcNow()</c>,
/// so an offset column would hold zero on every row; a single integer also sorts and compares
/// identically on SQLite and PostgreSQL, which a provider-neutral model requires (FR-DATA-003).
///
/// Nothing here holds prompt text, model output, tool arguments, or a credential. The two JSON
/// columns carry the structural summaries, which are counts, shapes, and field <em>names</em> by
/// construction (FR-TRACE-003, FR-DATA-010).
/// </remarks>
internal sealed class ExchangeRow
{
    /// <summary>Identity assigned at ingress.</summary>
    public Guid ExchangeId { get; set; }

    /// <summary>The correlation token returned to the client.</summary>
    public string PublicRequestId { get; set; } = string.Empty;

    /// <summary>The trace identifier, or <c>null</c> when tracing produced no activity.</summary>
    public string? TraceId { get; set; }

    /// <summary>Which client-facing protocol the request arrived on.</summary>
    public int IngressProtocol { get; set; }

    /// <summary>When the request was accepted, as UTC ticks.</summary>
    public long StartedAtTicks { get; set; }

    /// <summary>When the exchange reached a terminal state, or <c>null</c> when no boundary was observed.</summary>
    public long? CompletedAtTicks { get; set; }

    /// <summary>The model the client requested, or <c>null</c> when the request failed before it was known.</summary>
    public string? ClientModelId { get; set; }

    /// <summary>The runtime that served the exchange, or <c>null</c> when routing never chose one.</summary>
    public string? RuntimeEndpointId { get; set; }

    /// <summary>The model identifier sent upstream, when routing resolved one.</summary>
    public string? UpstreamModelId { get; set; }

    /// <summary>How the model was resolved, when it was.</summary>
    public int? ResolutionSource { get; set; }

    /// <summary>The alias that produced the resolution, when one did.</summary>
    public string? ResolutionAliasId { get; set; }

    /// <summary>
    /// Whether the client asked for a stream, or <c>null</c> when the request was refused before its
    /// envelope was read and therefore never stated a preference.
    /// </summary>
    public bool? Streaming { get; set; }

    /// <summary>True once AgentSplice committed a streamed response to the client.</summary>
    public bool StreamedResponse { get; set; }

    /// <summary>Terminal lifecycle state.</summary>
    public int Status { get; set; }

    /// <summary>Why the exchange failed, or <c>null</c> when it did not.</summary>
    public int? FailureClass { get; set; }

    /// <summary>
    /// The stable error code reported to the client, when one was.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="FailureClass"/> rather than derived from it. The class is the
    /// internal classification; the code is the string the client actually received and the one an
    /// operator's issue report will quote.
    /// </remarks>
    public string? ErrorCode { get; set; }

    /// <summary>How the stream ended.</summary>
    public int StreamTermination { get; set; }

    /// <summary>What was retained for this exchange (FR-TRACE-010).</summary>
    public int ContentRetentionState { get; set; }

    /// <summary>Identifier of a captured environment snapshot, when hardware metadata was collected.</summary>
    public string? EnvironmentSnapshotId { get; set; }

    /// <summary>The status the runtime returned, when a response was observed.</summary>
    public int? UpstreamStatusCode { get; set; }

    /// <summary>
    /// The normalised upstream media type, parameters stripped.
    /// </summary>
    /// <remarks>
    /// Never <c>RelayableContentType</c>. That value is unbounded runtime-chosen text whose only
    /// destination is the wire, and <see cref="Domain.Exchanges.UpstreamResponseMetadata"/> states
    /// outright that it must not reach evidence.
    /// </remarks>
    public string? UpstreamMediaType { get; set; }

    /// <summary>The runtime's own request identifier, when it sent one (FR-CHAT-010).</summary>
    public string? UpstreamRequestId { get; set; }

    /// <summary>The structural request summary as a JSON document, or <c>null</c> when none was built.</summary>
    public string? RequestSummaryJson { get; set; }

    /// <summary>The structural response summary as a JSON document, or <c>null</c> when the body was not interpretable.</summary>
    public string? ResponseSummaryJson { get; set; }

    /// <summary>Token usage with per-component provenance, or <c>null</c> when nothing reported any.</summary>
    public string? UsageJson { get; set; }

    /// <summary>The timeline, in sequence order.</summary>
    public ICollection<ExchangeObservationRow> Observations { get; } = new List<ExchangeObservationRow>();

    /// <summary>The values derived from the timeline, each with its provenance.</summary>
    public ICollection<ExchangeMeasurementRow> Measurements { get; } = new List<ExchangeMeasurementRow>();
}
