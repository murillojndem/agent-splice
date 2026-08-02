using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Administration;

/// <summary>
/// One exchange as the administrative list presents it (openapi <c>ExchangeSummary</c>).
/// </summary>
/// <remarks>
/// A view rather than a domain record, and rather than a persistence row. The store holds rows the
/// domain cannot express — a request refused before its envelope was read has no
/// <see cref="CompletionExchange"/> — so a read path that returned domain records would have to
/// invent what those requests never had, and one that returned rows would put column names on the
/// wire.
///
/// Every optional member is genuinely optional. A value the exchange never had is absent here rather
/// than defaulted, because the client of this API is a diagnostic surface and a zero it cannot
/// distinguish from a measurement is worse than a gap (FR-DASH-006, FR-TRACE-006).
/// </remarks>
public sealed record ExchangeSummaryView
{
    /// <summary>Identity assigned at ingress.</summary>
    public required ExchangeId ExchangeId { get; init; }

    /// <summary>The correlation token returned to the client.</summary>
    public required string RequestId { get; init; }

    /// <summary>The trace identifier, when tracing produced one.</summary>
    public string? TraceId { get; init; }

    /// <summary>When the request was accepted.</summary>
    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>When the exchange ended, or <c>null</c> when no boundary recorded it.</summary>
    public DateTimeOffset? CompletedAt { get; init; }

    /// <summary>Terminal lifecycle state.</summary>
    public required ExchangeStatus Status { get; init; }

    /// <summary>The runtime that served it, when routing chose one.</summary>
    public string? RuntimeId { get; init; }

    /// <summary>The model the client asked for, when the request got far enough to name one.</summary>
    public string? ClientModelId { get; init; }

    /// <summary>The model sent upstream, when routing resolved one.</summary>
    public string? UpstreamModelId { get; init; }

    /// <summary>
    /// Whether the client asked for a stream, or <c>null</c> when it never stated a preference.
    /// </summary>
    public bool? Streaming { get; init; }

    /// <summary>What was retained for this exchange.</summary>
    public required ContentRetentionState ContentRetentionState { get; init; }
}

/// <summary>One exchange in full (openapi <c>ExchangeDetail</c>).</summary>
public sealed record ExchangeDetailView
{
    /// <summary>Everything the list shows.</summary>
    public required ExchangeSummaryView Summary { get; init; }

    /// <summary>Which client-facing protocol the request arrived on.</summary>
    public required IngressProtocol IngressProtocol { get; init; }

    /// <summary>How the stream ended.</summary>
    public required StreamTermination StreamTermination { get; init; }

    /// <summary>Why the exchange failed, or <c>null</c> when it did not.</summary>
    public FailureClass? FailureClass { get; init; }

    /// <summary>The stable error code the client received, when one was sent.</summary>
    public string? ErrorCode { get; init; }

    /// <summary>The status the runtime returned, when a response was observed.</summary>
    public int? UpstreamStatusCode { get; init; }

    /// <summary>The values derived from the timeline, each with its provenance.</summary>
    public required IReadOnlyList<MeasurementView> Measurements { get; init; }

    /// <summary>
    /// The structural request summary, as the stored JSON document.
    /// </summary>
    /// <remarks>
    /// Passed through rather than reprojected. The schema declares it
    /// <c>additionalProperties: true</c> precisely so a summary can gain a field without a contract
    /// change, and re-serialising it here would make this type the place that has to keep up.
    /// </remarks>
    public string? RequestSummaryJson { get; init; }

    /// <summary>The structural response summary, as the stored JSON document.</summary>
    public string? ResponseSummaryJson { get; init; }

    /// <summary>Token usage with per-component provenance, as the stored JSON document.</summary>
    public string? UsageJson { get; init; }
}

/// <summary>One measurement (openapi <c>Measurement</c>).</summary>
public sealed record MeasurementView
{
    /// <summary>Stable measurement name.</summary>
    public required string Name { get; init; }

    /// <summary>The value. Always finite.</summary>
    public required double Value { get; init; }

    /// <summary>The unit of <see cref="Value"/>.</summary>
    public required Domain.Measurements.MeasurementUnit Unit { get; init; }

    /// <summary>Where the value came from. Never absent.</summary>
    public required Domain.Measurements.MeasurementProvenance Provenance { get; init; }

    /// <summary>Confidence in [0,1], when the provenance is weaker than a direct observation.</summary>
    public double? Confidence { get; init; }
}

/// <summary>One timeline boundary (openapi <c>TimelineObservation</c>).</summary>
public sealed record TimelineObservationView
{
    /// <summary>Zero-based position in the exchange timeline.</summary>
    public required int Sequence { get; init; }

    /// <summary>Which boundary was observed.</summary>
    public required Domain.Observations.ObservationType Type { get; init; }

    /// <summary>When the boundary was observed.</summary>
    public required DateTimeOffset Timestamp { get; init; }

    /// <summary>Where the evidence came from.</summary>
    public required Domain.Observations.ObservationSource Source { get; init; }

    /// <summary>Confidence in [0,1], present only when the source is not a direct observation.</summary>
    public double? Confidence { get; init; }

    /// <summary>The sanitised detail map, as the stored JSON document.</summary>
    public string? DetailsJson { get; init; }
}

/// <summary>A page of exchanges and the cursor that continues it (openapi <c>ExchangePage</c>).</summary>
public sealed record ExchangePageView
{
    /// <summary>The page, newest first.</summary>
    public required IReadOnlyList<ExchangeSummaryView> Items { get; init; }

    /// <summary>The cursor for the next page, or <c>null</c> when this is the last one.</summary>
    public string? NextCursor { get; init; }
}
