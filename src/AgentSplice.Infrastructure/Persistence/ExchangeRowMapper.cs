using AgentSplice.Application.Exchanges;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;
using AgentSplice.Infrastructure.Persistence.Rows;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// Projects the evidence gathered for one request into storable rows.
/// </summary>
/// <remarks>
/// One direction only. Nothing maps a row back to a <see cref="CompletionExchange"/>: the read path
/// serves views, and reconstructing the domain aggregate would mean either inventing the values a
/// model-less request never had or refusing to list it at all — the gap ADR 0008 left for this stage.
///
/// Absence is preserved everywhere. A boundary that was not observed produces no observation row, a
/// measurement that could not be derived produces no measurement row, and a value the runtime never
/// reported stays <c>null</c> rather than becoming a zero (FR-TRACE-006).
/// </remarks>
internal static class ExchangeRowMapper
{
    /// <summary>Projects a completed request's evidence into a row graph.</summary>
    /// <param name="record">The evidence gathered for one request.</param>
    /// <param name="retention">
    /// What this write actually retains. Supplied by the caller rather than copied from the exchange,
    /// because the exchange cannot know: <c>ExchangeRecorder.Accept</c> opens every exchange as
    /// <see cref="ContentRetentionState.Disabled"/> and the gateway hands the record to whichever sink
    /// is registered without being told which one that is. Copying it through produced a store full of
    /// rows carrying summaries, observations, and measurements while declaring that nothing was
    /// retained — the value FR-TRACE-010 exists to make readable, and systematically wrong.
    /// </param>
    internal static ExchangeRow ToRow(ExchangeRecord record, ContentRetentionState retention)
    {
        ArgumentNullException.ThrowIfNull(record);

        if (!Enum.IsDefined(retention))
        {
            throw new ArgumentOutOfRangeException(nameof(retention), retention, "Unknown content retention state.");
        }

        var exchange = record.Exchange;
        var row = new ExchangeRow
        {
            ExchangeId = record.ExchangeId.Value,
            PublicRequestId = record.RequestId.Value,
            TraceId = exchange?.TraceId?.Value,
            IngressProtocol = (int)(exchange?.IngressProtocol ?? IngressProtocol.OpenAiChatCompletions),
            StartedAtTicks = StartedAt(record).UtcTicks,
            CompletedAtTicks = CompletedAt(record)?.UtcTicks,
            ClientModelId = exchange?.ClientModelId.Value,
            RuntimeEndpointId = exchange?.Resolution?.Runtime.Value,
            UpstreamModelId = exchange?.Resolution?.UpstreamModel.Value,
            ResolutionSource = exchange?.Resolution is { } resolution ? (int)resolution.Source : null,
            ResolutionAliasId = exchange?.Resolution?.Alias?.Value,
            Streaming = exchange?.Streaming,
            StreamedResponse = exchange?.StreamedResponse ?? false,
            Status = (int)Status(record),
            FailureClass = FailureClassOf(record) is { } failure ? (int)failure : null,
            ErrorCode = record.Error?.Code,
            StreamTermination = (int)(exchange?.StreamTermination ?? StreamTermination.NotApplicable),
            ContentRetentionState = (int)retention,
            EnvironmentSnapshotId = exchange?.EnvironmentSnapshotId,
            UpstreamStatusCode = exchange?.UpstreamResponse?.StatusCode,
            UpstreamMediaType = exchange?.UpstreamResponse?.ContentType,
            UpstreamRequestId = exchange?.UpstreamResponse?.UpstreamRequestId,
            RequestSummaryJson = PersistenceJsonContext.Serialize(
                exchange?.RequestSummary,
                PersistenceJsonContext.Default.StructuralRequestSummary),
            ResponseSummaryJson = PersistenceJsonContext.Serialize(
                exchange?.ResponseSummary,
                PersistenceJsonContext.Default.StructuralResponseSummary),
            UsageJson = PersistenceJsonContext.Serialize(
                exchange is null ? null : PersistedUsage.From(exchange.Usage),
                PersistenceJsonContext.Default.PersistedUsage),
        };

        foreach (var observation in record.Observations)
        {
            row.Observations.Add(ToRow(observation));
        }

        foreach (var measurement in record.Measurements)
        {
            row.Measurements.Add(ToRow(measurement, record.ExchangeId.Value));
        }

        return row;
    }

    /// <summary>Projects one timeline boundary.</summary>
    internal static ExchangeObservationRow ToRow(ExchangeObservation observation)
    {
        ArgumentNullException.ThrowIfNull(observation);

        return new ExchangeObservationRow
        {
            ObservationId = observation.ObservationId.Value,
            ExchangeId = observation.ExchangeId.Value,
            Sequence = observation.Sequence,
            Type = (int)observation.Type,
            TimestampTicks = observation.Timestamp.UtcTicks,
            Source = (int)observation.Source,
            DurationTicks = observation.Duration?.Ticks,
            Confidence = observation.Confidence,
            DetailsJson = observation.Details.IsEmpty
                ? null
                : PersistenceJsonContext.Serialize(
                    observation.Details.Values,
                    PersistenceJsonContext.Default.IReadOnlyDictionaryStringString),
        };
    }

    /// <summary>Projects one measurement.</summary>
    internal static ExchangeMeasurementRow ToRow(Measurement measurement, Guid exchangeId)
    {
        ArgumentNullException.ThrowIfNull(measurement);

        return new ExchangeMeasurementRow
        {
            MeasurementId = measurement.MeasurementId.Value,
            ExchangeId = measurement.ExchangeId?.Value ?? exchangeId,
            Name = measurement.Name,
            Value = measurement.Value,
            Unit = (int)measurement.Unit,
            Provenance = (int)measurement.Provenance,
            Confidence = measurement.Confidence,
            StartedAtTicks = measurement.StartedAt?.UtcTicks,
            EndedAtTicks = measurement.EndedAt?.UtcTicks,
        };
    }

    /// <summary>
    /// When the request was accepted.
    /// </summary>
    /// <remarks>
    /// Taken from the exchange when one exists, and otherwise from the boundary that named the event.
    /// A request refused before its model was known still has a <see cref="ObservationType.RequestAccepted"/>
    /// observation, because the gateway stamps it from the transport's own reading before anything is
    /// parsed. Falling back to the first observation covers only the case where even that is missing,
    /// which no current path produces.
    /// </remarks>
    private static DateTimeOffset StartedAt(ExchangeRecord record)
    {
        if (record.Exchange is { } exchange)
        {
            return exchange.StartedAt;
        }

        if (Find(record, ObservationType.RequestAccepted) is { } accepted)
        {
            return accepted.Timestamp;
        }

        return record.Observations.Count > 0 ? record.Observations[0].Timestamp : default;
    }

    /// <summary>
    /// When the exchange ended, or <c>null</c> when no boundary recorded it.
    /// </summary>
    /// <remarks>
    /// A failed model-less request legitimately has none: <c>ExchangeRecorder.Fail</c> appends no
    /// boundary of its own, because <see cref="ObservationType"/> has no "failed" member and inventing
    /// one would say less than the specific boundary the caller already recorded. A terminal status
    /// with no completion timestamp is unambiguous — it ended, and the moment was not observed — and
    /// is the honest answer rather than the current clock (FR-TRACE-006).
    /// </remarks>
    private static DateTimeOffset? CompletedAt(ExchangeRecord record) =>
        record.Exchange?.CompletedAt
            ?? Find(record, ObservationType.ClientCompleted)?.Timestamp
            ?? Find(record, ObservationType.ClientCancelled)?.Timestamp;

    /// <summary>
    /// The terminal state, including for a request that never produced an exchange.
    /// </summary>
    /// <remarks>
    /// Classified from the timeline rather than from the absence of an error. A client that
    /// disconnected before its model was known reaches the sink with no exchange <em>and</em> no
    /// error, so inferring "failed" from a missing error would record a cancellation as a gateway
    /// fault — the same distinction <c>ChatCompletionGateway</c> preserves for exchanges that do
    /// exist.
    /// </remarks>
    private static ExchangeStatus Status(ExchangeRecord record) =>
        record.Exchange?.Status
            ?? (Find(record, ObservationType.ClientCancelled) is not null
                ? ExchangeStatus.Cancelled
                : ExchangeStatus.Failed);

    private static FailureClass? FailureClassOf(ExchangeRecord record) =>
        record.Exchange?.FailureClass ?? record.Error?.FailureClass;

    private static ExchangeObservation? Find(ExchangeRecord record, ObservationType type)
    {
        foreach (var observation in record.Observations)
        {
            if (observation.Type == type)
            {
                return observation;
            }
        }

        return null;
    }
}
