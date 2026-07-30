using AgentSplice.Application.Errors;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
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
/// It also enforces the rule that a request reaches exactly one terminal state. Recording two would
/// either throw from the domain or silently overwrite the first, and both are worse than a guard
/// here.
/// </remarks>
public sealed class ExchangeRecorder
{
    private readonly ExchangeTimeline timeline;
    private readonly TimeProvider timeProvider;
    private bool terminated;

    /// <summary>Opens a recorder for a request.</summary>
    public ExchangeRecorder(ExchangeId exchangeId, PublicRequestId requestId, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        if (exchangeId.IsEmpty)
        {
            throw new ArgumentException("A recorder requires an exchange identity.", nameof(exchangeId));
        }

        ExchangeId = exchangeId;
        RequestId = requestId;
        this.timeProvider = timeProvider;
        timeline = new ExchangeTimeline(exchangeId);
    }

    /// <summary>Identity assigned at ingress.</summary>
    public ExchangeId ExchangeId { get; }

    /// <summary>The correlation token returned to the client.</summary>
    public PublicRequestId RequestId { get; }

    /// <summary>The exchange, once the requested model was known.</summary>
    public CompletionExchange? Exchange { get; private set; }

    /// <summary>The error reported, once one was.</summary>
    public GatewayError? Error { get; private set; }

    /// <summary>The current time from the injected clock.</summary>
    public DateTimeOffset Now => timeProvider.GetUtcNow();

    /// <summary>Appends a timeline boundary.</summary>
    public void Observe(ObservationType type, SafeDetails? details = null) =>
        timeline.Append(type, Now, ObservationSource.Gateway, details: details);

    /// <summary>Opens the exchange, once the requested model is known.</summary>
    public void Accept(ClientModelId model, bool streaming, DateTimeOffset startedAt, TraceId? traceId = null) =>
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
            traceId);

    /// <summary>Applies a transition to the exchange, if one exists.</summary>
    public void Update(Func<CompletionExchange, CompletionExchange> transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        if (Exchange is { } exchange)
        {
            Exchange = transition(exchange);
        }
    }

    /// <summary>Records normal completion of the transport cycle.</summary>
    /// <remarks>
    /// "Completed" says the cycle finished, not that the runtime succeeded. A relayed 429 or 500
    /// completes here with no failure class, because AgentSplice did not fail.
    /// </remarks>
    public void Complete(SafeDetails? details = null)
    {
        if (!TryTerminate())
        {
            return;
        }

        Observe(ObservationType.ClientCompleted, details);
        Update(exchange => exchange.Complete(Now));
    }

    /// <summary>
    /// Records a failure and the error the client will see.
    /// </summary>
    /// <remarks>
    /// Appends no boundary of its own. <see cref="ObservationType"/> has no "failed" member, and
    /// inventing one here would say less than the specific boundary the caller already recorded —
    /// a fired timeout phase, or an upstream response whose body could not be read.
    /// </remarks>
    public void Fail(GatewayError gatewayError)
    {
        ArgumentNullException.ThrowIfNull(gatewayError);

        Error = gatewayError;

        if (TryTerminate() && gatewayError.FailureClass is { } failureClass)
        {
            Update(exchange => exchange.Fail(failureClass, Now));
        }
    }

    /// <summary>Records client cancellation.</summary>
    public void Cancel(SafeDetails? details = null)
    {
        if (!TryTerminate())
        {
            return;
        }

        Observe(ObservationType.ClientCancelled, details);
        Update(exchange => exchange.Cancel(Now));
    }

    /// <summary>Produces the evidence record for this request.</summary>
    public ExchangeRecord ToRecord() =>
        ExchangeRecord.Create(ExchangeId, RequestId, timeline.Observations, Exchange, Error);

    private bool TryTerminate()
    {
        if (terminated)
        {
            return false;
        }

        terminated = true;
        return true;
    }
}
