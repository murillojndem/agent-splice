using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;

namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// The primary durable observation unit: one client completion request and its outcome
/// (docs/SPECIFICATION.md section 13.3, docs/ARCHITECTURE.md "Exchange and timeline model").
/// </summary>
/// <remarks>
/// Immutable. Every lifecycle step returns a new instance, so a persisted exchange can never be
/// mutated after the fact and an illegal transition fails loudly instead of silently overwriting
/// evidence. Fields that were never observed stay <c>null</c>, per FR-TRACE-006.
/// </remarks>
public sealed record CompletionExchange
{
    private CompletionExchange()
    {
    }

    /// <summary>Identity of this exchange.</summary>
    public ExchangeId ExchangeId { get; private init; }

    /// <summary>The correlation token returned to the client.</summary>
    public PublicRequestId PublicRequestId { get; private init; }

    /// <summary>The OpenTelemetry trace identifier, when tracing produced one.</summary>
    public TraceId? TraceId { get; private init; }

    /// <summary>Which client-facing protocol the request arrived on.</summary>
    public IngressProtocol IngressProtocol { get; private init; }

    /// <summary>When the request was accepted.</summary>
    public DateTimeOffset StartedAt { get; private init; }

    /// <summary>When the exchange reached a terminal state, or <c>null</c> while it is in flight.</summary>
    public DateTimeOffset? CompletedAt { get; private init; }

    /// <summary>The model identifier the client requested.</summary>
    public ClientModelId ClientModelId { get; private init; }

    /// <summary>The resolution outcome, or <c>null</c> if the exchange failed before routing.</summary>
    public ModelResolution? Resolution { get; private init; }

    /// <summary>The runtime endpoint used, or <c>null</c> if the exchange failed before routing.</summary>
    public RuntimeEndpointId? RuntimeEndpointId => Resolution?.Runtime;

    /// <summary>The upstream model used, or <c>null</c> if the exchange failed before routing.</summary>
    public UpstreamModelId? UpstreamModelId => Resolution?.UpstreamModel;

    /// <summary>True when the client requested a streamed response.</summary>
    public bool Streaming { get; private init; }

    /// <summary>Lifecycle state.</summary>
    public ExchangeStatus Status { get; private init; }

    /// <summary>Why the exchange failed, or <c>null</c> when it did not.</summary>
    public FailureClass? FailureClass { get; private init; }

    /// <summary>How the stream ended.</summary>
    public StreamTermination StreamTermination { get; private init; }

    /// <summary>What was retained for this exchange.</summary>
    public ContentRetentionState ContentRetentionState { get; private init; }

    /// <summary>Structural request summary, once created.</summary>
    public StructuralRequestSummary? RequestSummary { get; private init; }

    /// <summary>Structural response summary, once the response body has been interpreted.</summary>
    public StructuralResponseSummary? ResponseSummary { get; private init; }

    /// <summary>
    /// What the runtime's response headers said, once they were observed. Present even when the body
    /// could not be interpreted and <see cref="ResponseSummary"/> is therefore absent.
    /// </summary>
    public UpstreamResponseMetadata? UpstreamResponse { get; private init; }

    /// <summary>Token usage with per-component provenance. <see cref="UsageObservation.Unknown"/> until reported.</summary>
    public UsageObservation Usage { get; private init; } = UsageObservation.Unknown;

    /// <summary>Identifier of a captured environment snapshot, when hardware metadata was collected.</summary>
    public string? EnvironmentSnapshotId { get; private init; }

    /// <summary>True when the exchange has reached a terminal state.</summary>
    public bool IsTerminal =>
        Status is ExchangeStatus.Completed or ExchangeStatus.Cancelled or ExchangeStatus.Failed;

    /// <summary>Opens an exchange in the <see cref="ExchangeStatus.Accepted"/> state.</summary>
    /// <param name="exchangeId">Identity assigned at ingress.</param>
    /// <param name="publicRequestId">Correlation token returned to the client.</param>
    /// <param name="ingressProtocol">Client-facing protocol.</param>
    /// <param name="clientModelId">Model identifier requested by the client.</param>
    /// <param name="streaming">Whether a streamed response was requested.</param>
    /// <param name="startedAt">Acceptance timestamp, taken from <see cref="TimeProvider"/>.</param>
    /// <param name="contentRetentionState">
    /// Retention state for this exchange. Defaults to <see cref="ContentRetentionState.Disabled"/>
    /// so that an exchange created without an explicit decision claims no retention (FR-DATA-005).
    /// </param>
    /// <param name="traceId">Trace identifier, when one exists.</param>
    public static CompletionExchange Accept(
        ExchangeId exchangeId,
        PublicRequestId publicRequestId,
        IngressProtocol ingressProtocol,
        ClientModelId clientModelId,
        bool streaming,
        DateTimeOffset startedAt,
        ContentRetentionState contentRetentionState = ContentRetentionState.Disabled,
        TraceId? traceId = null)
    {
        if (exchangeId.IsEmpty)
        {
            throw new ArgumentException("An exchange requires an identity.", nameof(exchangeId));
        }

        if (publicRequestId.IsEmpty)
        {
            throw new ArgumentException("An exchange requires a public request identifier.", nameof(publicRequestId));
        }

        if (clientModelId.IsEmpty)
        {
            throw new ArgumentException("An exchange requires the requested model identifier.", nameof(clientModelId));
        }

        if (!Enum.IsDefined(ingressProtocol))
        {
            throw new ArgumentOutOfRangeException(
                nameof(ingressProtocol),
                ingressProtocol,
                "Unknown ingress protocol.");
        }

        if (!Enum.IsDefined(contentRetentionState))
        {
            throw new ArgumentOutOfRangeException(
                nameof(contentRetentionState),
                contentRetentionState,
                "Unknown content retention state.");
        }

        return new CompletionExchange
        {
            ExchangeId = exchangeId,
            PublicRequestId = publicRequestId,
            TraceId = traceId,
            IngressProtocol = ingressProtocol,
            StartedAt = startedAt,
            ClientModelId = clientModelId,
            Streaming = streaming,
            Status = ExchangeStatus.Accepted,
            StreamTermination = streaming ? StreamTermination.Unknown : StreamTermination.NotApplicable,
            ContentRetentionState = contentRetentionState,
        };
    }

    /// <summary>Attaches the structural request summary.</summary>
    public CompletionExchange WithRequestSummary(StructuralRequestSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return this with { RequestSummary = summary };
    }

    /// <summary>Attaches the structural response summary.</summary>
    public CompletionExchange WithResponseSummary(StructuralResponseSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return this with { ResponseSummary = summary };
    }

    /// <summary>Records what the runtime's response headers said.</summary>
    public CompletionExchange WithUpstreamResponse(UpstreamResponseMetadata metadata)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        return this with { UpstreamResponse = metadata };
    }

    /// <summary>Attaches token usage.</summary>
    public CompletionExchange WithUsage(UsageObservation usage)
    {
        ArgumentNullException.ThrowIfNull(usage);
        return this with { Usage = usage };
    }

    /// <summary>Attaches a captured environment snapshot identifier.</summary>
    public CompletionExchange WithEnvironmentSnapshot(string environmentSnapshotId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(environmentSnapshotId);
        return this with { EnvironmentSnapshotId = environmentSnapshotId.Trim() };
    }

    /// <summary>Records the retention decision applied to this exchange.</summary>
    public CompletionExchange WithContentRetentionState(ContentRetentionState state)
    {
        if (!Enum.IsDefined(state))
        {
            throw new ArgumentOutOfRangeException(nameof(state), state, "Unknown content retention state.");
        }

        return this with { ContentRetentionState = state };
    }

    /// <summary>Records model and runtime resolution and moves to <see cref="ExchangeStatus.Forwarding"/>.</summary>
    public CompletionExchange Resolve(ModelResolution resolution)
    {
        ArgumentNullException.ThrowIfNull(resolution);
        EnsureNotTerminal(nameof(Resolve));

        if (!string.Equals(resolution.ClientModel.Value, ClientModelId.Value, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The resolution describes a different requested model than this exchange.",
                nameof(resolution));
        }

        return this with { Resolution = resolution, Status = ExchangeStatus.Forwarding };
    }

    /// <summary>Records that bytes have begun flowing to the client.</summary>
    public CompletionExchange BeginStreaming()
    {
        EnsureNotTerminal(nameof(BeginStreaming));

        if (!Streaming)
        {
            throw new InvalidOperationException(
                "A non-streamed exchange cannot enter the streaming state.");
        }

        return this with { Status = ExchangeStatus.Streaming };
    }

    /// <summary>Completes the exchange normally.</summary>
    /// <param name="completedAt">Completion timestamp, taken from <see cref="TimeProvider"/>.</param>
    /// <param name="streamTermination">
    /// How the stream ended. Must be <see cref="StreamTermination.NotApplicable"/> for a
    /// non-streamed exchange.
    /// </param>
    public CompletionExchange Complete(
        DateTimeOffset completedAt,
        StreamTermination streamTermination = StreamTermination.NotApplicable)
    {
        EnsureNotTerminal(nameof(Complete));
        EnsureTerminationMatchesMode(streamTermination);

        return this with
        {
            Status = ExchangeStatus.Completed,
            CompletedAt = completedAt,
            StreamTermination = streamTermination,
        };
    }

    /// <summary>Records client cancellation or disconnect.</summary>
    public CompletionExchange Cancel(DateTimeOffset cancelledAt)
    {
        EnsureNotTerminal(nameof(Cancel));

        return this with
        {
            Status = ExchangeStatus.Cancelled,
            FailureClass = Exchanges.FailureClass.RequestCancelled,
            CompletedAt = cancelledAt,
            StreamTermination = Streaming ? StreamTermination.ClientCancelled : StreamTermination.NotApplicable,
        };
    }

    /// <summary>Records a failure with its stable class.</summary>
    public CompletionExchange Fail(
        FailureClass failureClass,
        DateTimeOffset failedAt,
        StreamTermination? streamTermination = null)
    {
        EnsureNotTerminal(nameof(Fail));

        if (!Enum.IsDefined(failureClass))
        {
            throw new ArgumentOutOfRangeException(nameof(failureClass), failureClass, "Unknown failure class.");
        }

        var termination = streamTermination
            ?? (Streaming ? StreamTermination.Unknown : StreamTermination.NotApplicable);

        EnsureTerminationMatchesMode(termination);

        return this with
        {
            Status = ExchangeStatus.Failed,
            FailureClass = failureClass,
            CompletedAt = failedAt,
            StreamTermination = termination,
        };
    }

    private void EnsureNotTerminal(string operation)
    {
        if (IsTerminal)
        {
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"'{operation}' is not valid for an exchange already in the terminal state '{Status}'."));
        }
    }

    private void EnsureTerminationMatchesMode(StreamTermination streamTermination)
    {
        if (!Enum.IsDefined(streamTermination))
        {
            throw new ArgumentOutOfRangeException(
                nameof(streamTermination),
                streamTermination,
                "Unknown stream termination.");
        }

        if (Streaming && streamTermination == StreamTermination.NotApplicable)
        {
            throw new ArgumentException(
                "A streamed exchange must record how its stream ended.",
                nameof(streamTermination));
        }

        if (!Streaming && streamTermination != StreamTermination.NotApplicable)
        {
            throw new ArgumentException(
                "A non-streamed exchange has no stream termination to record.",
                nameof(streamTermination));
        }
    }
}
