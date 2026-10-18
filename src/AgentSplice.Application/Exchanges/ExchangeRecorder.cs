using AgentSplice.Application.Errors;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
using AgentSplice.Domain.Observations;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// Holds the exchange and its timeline for one request, and keeps the two consistent.
/// </summary>
/// <remarks>
/// <see cref="CompletionExchange"/> is immutable and every transition returns a new instance, which
/// is right for evidence but awkward for a request path that has to thread the current value through
/// a dozen steps. This is the one mutable holder, so the immutability of the evidence itself is
/// preserved while the orchestrator stays readable.
///
/// It also enforces the rule that a request reaches exactly one terminal state, and that the state is
/// recorded together with the client-side boundary that ended it: <see cref="ObservationType.ClientCompleted"/>,
/// <see cref="ObservationType.ClientCancelled"/>, or <see cref="ObservationType.ClientResponseAbandoned"/>.
/// Recording two would either throw from the domain or silently overwrite the first, and both are
/// worse than a guard here.
///
/// One recorder belongs to one request and is used from a single logical asynchronous flow, so the
/// awaits that carry it between thread-pool threads carry its visibility with them. The terminal and
/// recording guards are interlocked regardless, because those are the two decisions where a duplicate
/// corrupts evidence rather than merely repeating work. Nothing here may be called from a cancellation
/// callback: that runs concurrently with the flow and would break the invariant.
/// </remarks>
public sealed class ExchangeRecorder
{
    private const string ClientResponseKey = "client.response";
    private const string RelayedResponse = "relayed";
    private const string GatewayErrorResponse = "gateway_error";

    /// <summary>The boundaries that end an exchange on the client's side; exactly one is recorded.</summary>
    private static readonly ObservationType[] TerminalClientBoundaries =
    [
        ObservationType.ClientCompleted,
        ObservationType.ClientCancelled,
        ObservationType.ClientResponseAbandoned,
    ];

    private readonly ExchangeTimeline timeline;
    private readonly TimeProvider timeProvider;
    private int terminated;
    private int recorded;

    /// <summary>Opens a recorder for a request.</summary>
    public ExchangeRecorder(
        ExchangeId exchangeId,
        PublicRequestId requestId,
        TimeProvider timeProvider,
        Observability.IExchangeTrace? trace = null)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (exchangeId.IsEmpty)
        {
            throw new ArgumentException("A recorder requires an exchange identity.", nameof(exchangeId));
        }

        ExchangeId = exchangeId;
        RequestId = requestId;
        Trace = trace;
        TraceId = trace?.TraceId;
        this.timeProvider = timeProvider;
        timeline = new ExchangeTimeline(exchangeId);
    }

    /// <summary>Identity assigned at ingress.</summary>
    public ExchangeId ExchangeId { get; }

    /// <summary>The correlation token returned to the client.</summary>
    public PublicRequestId RequestId { get; }

    /// <summary>The trace identifier, or <c>null</c> when tracing produced no activity.</summary>
    public TraceId? TraceId { get; }

    /// <summary>The span covering this exchange, when one exists.</summary>
    public Observability.IExchangeTrace? Trace { get; }

    /// <summary>The exchange, once the requested model was known.</summary>
    public CompletionExchange? Exchange { get; private set; }

    /// <summary>The error envelope the client received, once one was written.</summary>
    public GatewayError? Error { get; private set; }

    /// <summary>
    /// Why the exchange failed, whether or not an envelope saying so reached the client.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="Error"/> because a response abandoned after it started failed for a
    /// reason the span and the metrics still have to carry, while no code reached the client and none
    /// may be recorded as though it had (ADR 0025).
    /// </remarks>
    public GatewayError? Failure { get; private set; }

    /// <summary>
    /// Bytes of the runtime's answer handed to the client's connection, or <c>null</c> when the
    /// client received no relayed answer.
    /// </summary>
    public long? RelayedClientBytes { get; private set; }

    /// <summary>The current time from the injected clock.</summary>
    public DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>The provider serving the resolved runtime, once routing has chosen one.</summary>
    public string? ProviderKey { get; private set; }

    /// <summary>True once the exchange has reached its terminal state.</summary>
    public bool IsTerminated => Volatile.Read(ref terminated) == 1;

    /// <summary>Appends a timeline boundary at the current time.</summary>
    public void Observe(ObservationType type, SafeDetails? details = null) =>
        Observe(type, Now, details);

    /// <summary>Appends a timeline boundary at a moment already observed elsewhere.</summary>
    /// <remarks>
    /// The source stays <see cref="ObservationSource.Gateway"/>: whoever supplied the timestamp read
    /// AgentSplice's own clock, just closer to the event than the orchestrator can. Without this
    /// overload every boundary is stamped when control returns rather than when the thing happened,
    /// which turns "time to response headers" into "time until the whole body had been read".
    /// </remarks>
    public void Observe(ObservationType type, DateTimeOffset timestamp, SafeDetails? details = null) =>
        timeline.Append(type, timestamp, ObservationSource.Gateway, details: details);

    /// <summary>Records how the upstream request failed.</summary>
    /// <remarks>
    /// A timeout as <see cref="ObservationType.TimeoutFired"/> with the phase that elapsed, wherever
    /// it elapsed: before the runtime's headers, while a buffered body was read, or mid-stream. Every
    /// other failure as <see cref="ObservationType.UpstreamFailed"/>, with its classification and the
    /// provider's bounded detail. A cancellation is the client's own doing and records nothing here;
    /// <see cref="ObservationType.ClientCancelled"/> says so (FR-CHAT-008, FR-TRACE-005).
    /// </remarks>
    public void ObserveUpstreamFailure(Runtimes.UpstreamFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.Reason == Runtimes.UpstreamFailureReason.Cancelled)
        {
            return;
        }

        if (failure.Phase is { } phase)
        {
            Observe(ObservationType.TimeoutFired, SafeDetails.Create("timeout.phase", phase.ToString()));
            return;
        }

        var entries = new List<KeyValuePair<string, string?>> { new("upstream.failure", failure.Reason.ToString()) };

        entries.AddRange(failure.Details.Values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));

        Observe(ObservationType.UpstreamFailed, SafeDetails.Create(entries));
    }

    /// <summary>True when the boundary has been recorded on this exchange's timeline.</summary>
    public bool HasObserved(ObservationType type) => timeline.Contains(type);

    /// <summary>
    /// Elapsed time between two boundaries, or <c>null</c> when either was never observed.
    /// </summary>
    /// <remarks>
    /// Returns <c>null</c> rather than zero so that a measurement derived from it is absent rather
    /// than reported as instantaneous (FR-TRACE-006, FR-OBS-004).
    /// </remarks>
    public TimeSpan? DurationBetween(ObservationType from, ObservationType to) =>
        timeline.DurationBetween(from, to);

    /// <summary>Records which runtime and provider will serve the exchange.</summary>
    public void SetRuntime(RuntimeEndpointId runtime, string providerKey)
    {
        ProviderKey = providerKey;
        Trace?.SetRuntime(runtime, providerKey);
    }

    /// <summary>Opens the exchange, once the requested model is known.</summary>
    public void Accept(ClientModelId model, bool streaming, DateTimeOffset startedAt) =>
        Exchange = CompletionExchange.Accept(
            ExchangeId,
            RequestId,
            IngressProtocol.OpenAiChatCompletions,
            model,
            streaming,
            startedAt,

            // Stage 1A retains nothing: there is no store, so claiming metadata retention would be
            // a claim about evidence that does not outlive the process (FR-DATA-005).
            ContentRetentionState.Disabled,
            TraceId);

    /// <summary>Applies a transition to the exchange, if one exists.</summary>
    public void Update(Func<CompletionExchange, CompletionExchange> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        if (Exchange is { } exchange)
        {
            Exchange = transition(exchange);
        }
    }

    /// <summary>Records that the runtime answered with an event stream, which is now being relayed.</summary>
    /// <remarks>
    /// Appends no boundary. It is a state change that decides which terminations are afterwards
    /// expressible, not an event: the runtime's headers are already on the timeline, and
    /// <see cref="ObservationType.ClientResponseStarted"/> records when the client's status was
    /// committed (ADR 0025).
    /// </remarks>
    public void BeginStreaming() => Update(exchange => exchange.BeginStreaming());

    /// <summary>Records how many bytes of the runtime's answer were handed to the client's connection.</summary>
    /// <remarks>
    /// Set by whatever wrote them, and only when it did. A buffered answer the client left in the
    /// middle of, and an error envelope sent in place of the runtime's answer, record none: the
    /// runtime's body length is a fact about the runtime, not about delivery.
    /// </remarks>
    public void RecordRelayedToClient(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(bytes);

        RelayedClientBytes = bytes;
    }

    /// <summary>Records that the relayed answer was written in full, and how its stream ended.</summary>
    /// <remarks>
    /// "Completed" says the cycle finished, not that the runtime succeeded. A relayed 429 or 500
    /// completes here with no failure class, because AgentSplice did not fail.
    /// </remarks>
    public void Complete(
        StreamTermination termination = StreamTermination.NotApplicable,
        SafeDetails? details = null)
    {
        if (IsTerminated)
        {
            return;
        }

        var at = Now;

        // The transition is computed before anything is recorded, so a transition the domain refuses
        // leaves the recorder open for the catch-all to end properly instead of a row that claims a
        // terminal boundary and keeps a non-terminal status.
        var next = Exchange?.Complete(at, termination);

        if (!TryTerminate())
        {
            return;
        }

        Observe(ObservationType.ClientCompleted, at, WithClientResponse(RelayedResponse, details));
        Exchange = next ?? Exchange;
    }

    /// <summary>Records a failure whose error envelope was written to the client.</summary>
    /// <remarks>
    /// Called after the write, never before it: <see cref="ExchangeRecord.Error"/> is the code the
    /// client received, and only a written envelope makes that true. A failure whose response had
    /// already started is <see cref="Abandon"/> instead (ADR 0025).
    /// </remarks>
    public void Fail(GatewayError gatewayError, StreamTermination? termination = null)
    {
        ArgumentNullException.ThrowIfNull(gatewayError);

        if (IsTerminated)
        {
            return;
        }

        var at = Now;
        var next = gatewayError.FailureClass is { } failureClass
            ? Exchange?.Fail(failureClass, at, termination)
            : null;

        if (!TryTerminate())
        {
            return;
        }

        Error = gatewayError;
        Failure = gatewayError;
        Observe(ObservationType.ClientCompleted, at, WithClientResponse(GatewayErrorResponse, details: null));
        Exchange = next ?? Exchange;
    }

    /// <summary>Records a failure whose response had already started, and was abandoned.</summary>
    /// <remarks>
    /// No error envelope reached the client, so none is recorded as <see cref="Error"/>. The failure
    /// class and the termination say why the response stopped; a code the client never received
    /// would claim otherwise (ADR 0025).
    /// </remarks>
    public void Abandon(GatewayError failure, StreamTermination termination)
    {
        ArgumentNullException.ThrowIfNull(failure);

        if (failure.FailureClass is not { } failureClass)
        {
            throw new ArgumentException("An abandoned response must record why it failed.", nameof(failure));
        }

        if (IsTerminated)
        {
            return;
        }

        var at = Now;
        var next = Exchange?.Fail(failureClass, at, termination);

        if (!TryTerminate())
        {
            return;
        }

        Failure = failure;
        Observe(ObservationType.ClientResponseAbandoned, at);
        Exchange = next ?? Exchange;
    }

    /// <summary>Records client cancellation.</summary>
    public void Cancel(SafeDetails? details = null)
    {
        if (IsTerminated)
        {
            return;
        }

        var at = Now;
        var next = Exchange?.Cancel(at);

        if (!TryTerminate())
        {
            return;
        }

        Observe(ObservationType.ClientCancelled, at, details);
        Exchange = next ?? Exchange;
    }

    /// <summary>
    /// The measurement of a given name, or <c>null</c> when the evidence did not support one.
    /// </summary>
    /// <remarks>
    /// Metrics read the derived measurement rather than recomputing it, so a value can never reach a
    /// histogram under conditions the measurement layer already refused to derive it under.
    /// </remarks>
    public Measurement? FindMeasurement(string name) =>
        BuildMeasurements().Find(measurement => string.Equals(measurement.Name, name, StringComparison.Ordinal));

    /// <summary>Produces the evidence record for this request.</summary>
    public ExchangeRecord ToRecord() =>
        ExchangeRecord.Create(
            ExchangeId,
            RequestId,
            timeline.Observations,
            BuildMeasurements(),
            Exchange,
            Error,
            TraceId);

    /// <summary>
    /// Derives the measurements the observed boundaries actually support.
    /// </summary>
    /// <remarks>
    /// Each entry is added only when both of its boundaries were observed, so a phase that did not
    /// happen produces no measurement rather than a zero — the difference between "took no time" and
    /// "we do not know" (FR-TRACE-006, FR-OBS-004).
    ///
    /// Durations carry <see cref="MeasurementProvenance.Measured"/> because AgentSplice read its own
    /// clock. Token counts carry whatever provenance the count itself arrived with, which for a
    /// runtime-reported usage object is <see cref="MeasurementProvenance.UpstreamReported"/> — never
    /// silently upgraded to measured.
    ///
    /// The total runs to whichever client-side boundary ended the exchange, so a failed or cancelled
    /// exchange has one too, and it agrees with the duration the metrics export for it.
    ///
    /// Generation throughput is derived only for an exchange that actually streamed, over the window
    /// from the first semantic output event to upstream completion — the interval during which the
    /// runtime was demonstrably generating. Prompt throughput is derived nowhere. A non-streamed
    /// exchange offers no boundary separating prompt processing from generation at all, and even a
    /// streamed one exposes no prefill-completion signal, so deriving it would mean borrowing the
    /// time-to-first-token interval and calling it prompt processing — the exact reporting error
    /// CLAUDE.md calls out (FR-OBS-005).
    /// </remarks>
    private List<Measurement> BuildMeasurements()
    {
        var measurements = new List<Measurement>();

        Add(measurements, MeasurementNames.ValidationDuration, ObservationType.RequestBodyRead, ObservationType.ValidationCompleted);
        Add(measurements, MeasurementNames.RoutingDuration, ObservationType.StructuralSummaryCreated, ObservationType.ModelResolved);
        Add(measurements, MeasurementNames.UpstreamConnectDuration, ObservationType.UpstreamConnectionStarted, ObservationType.UpstreamConnectionEstablished);
        Add(measurements, MeasurementNames.UpstreamHeadersDuration, ObservationType.UpstreamRequestOpened, ObservationType.UpstreamHeadersReceived);
        Add(measurements, MeasurementNames.TimeToFirstUpstreamByte, ObservationType.UpstreamRequestOpened, ObservationType.FirstUpstreamByte);
        Add(measurements, MeasurementNames.TimeToFirstSemanticEvent, ObservationType.RequestAccepted, ObservationType.FirstSemanticEvent);
        Add(measurements, MeasurementNames.TimeToFirstClientEvent, ObservationType.RequestAccepted, ObservationType.FirstClientEventFlushed);

        if (TerminalClientBoundary() is { } terminal)
        {
            Add(measurements, MeasurementNames.TotalDuration, ObservationType.RequestAccepted, terminal);
        }

        if (Exchange is not { } exchange)
        {
            return measurements;
        }

        if (RelayedClientBytes is { } relayed)
        {
            measurements.Add(Measurement.Bytes(MeasurementNames.ClientResponseBytes, relayed, ExchangeId));

            // Only for an exchange that streamed. A buffered response has no events, and a count of
            // zero would read as "it streamed, and produced nothing".
            if (exchange.StreamedResponse && exchange.ResponseSummary is { } response)
            {
                measurements.Add(Measurement.Count(MeasurementNames.ClientStreamEvents, response.StreamEventCount, ExchangeId));
            }
        }

        if (exchange.StreamedResponse
            && ThroughputCalculator.TryCalculateGenerationThroughput(
                exchange.Usage.CompletionTokens,
                timeline.DurationBetween(ObservationType.FirstSemanticEvent, ObservationType.UpstreamCompleted),
                ExchangeId) is { } generation)
        {
            measurements.Add(generation);
        }

        if (exchange.Usage.PromptTokens is { } prompt)
        {
            measurements.Add(Measurement.Tokens(MeasurementNames.PromptTokens, prompt, ExchangeId));
        }

        if (exchange.Usage.CompletionTokens is { } completion)
        {
            measurements.Add(Measurement.Tokens(MeasurementNames.CompletionTokens, completion, ExchangeId));
        }

        return measurements;
    }

    private ObservationType? TerminalClientBoundary()
    {
        foreach (var type in TerminalClientBoundaries)
        {
            if (timeline.Contains(type))
            {
                return type;
            }
        }

        return null;
    }

    private void Add(List<Measurement> measurements, string name, ObservationType from, ObservationType to)
    {
        if (timeline.DurationBetween(from, to) is { } elapsed && elapsed >= TimeSpan.Zero)
        {
            measurements.Add(Measurement.Duration(name, elapsed, ExchangeId));
        }
    }

    /// <summary>True the first time only: this exchange may be handed to the sink now.</summary>
    /// <remarks>
    /// Lives on the recorder rather than the gateway because the gateway is a singleton and holds no
    /// per-request state. Without the guard, a fault raised after the response was already written
    /// reaches the orchestrator's catch-all, which produces a second outcome and a second sink call
    /// for one exchange — two records of one request, disagreeing about how it ended.
    /// </remarks>
    public bool TryBeginRecording() => Interlocked.Exchange(ref recorded, 1) == 0;

    private bool TryTerminate() => Interlocked.Exchange(ref terminated, 1) == 0;

    private static SafeDetails WithClientResponse(string response, SafeDetails? details)
    {
        var entries = new List<KeyValuePair<string, string?>> { new(ClientResponseKey, response) };

        if (details is { } extra)
        {
            entries.AddRange(extra.Values.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));
        }

        return SafeDetails.Create(entries);
    }
}
