using AgentSplice.Application.Errors;
using AgentSplice.Application.Models;
using AgentSplice.Application.Protocols;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Observations;
using Microsoft.Extensions.Logging;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// The Stage 1A non-streaming request path (docs/ARCHITECTURE.md "Stage 1 primary request flow").
/// </summary>
/// <remarks>
/// The whole of the completion path lives here rather than in an endpoint, because an endpoint is
/// the one place none of it can be unit tested.
///
/// The order of the first steps is deliberate: the structural summary is built <em>before</em>
/// resolution, so a request that names an unknown model still leaves safe evidence of what arrived.
/// Building it afterwards would lose that evidence in precisely the case an operator most needs it.
/// </remarks>
public sealed class ChatCompletionGateway
{
    private const string JsonMediaType = "application/json";

    private readonly IChatCompletionRequestCodec requestCodec;
    private readonly IChatCompletionResponseCodec responseCodec;
    private readonly IErrorEnvelopeWriter errorWriter;
    private readonly ModelResolver resolver;
    private readonly ModelRuntimeProviderRegistry providers;
    private readonly IExchangeRecordSink sink;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ChatCompletionGateway> logger;

    /// <summary>Creates the gateway.</summary>
    public ChatCompletionGateway(
        IChatCompletionRequestCodec requestCodec,
        IChatCompletionResponseCodec responseCodec,
        IErrorEnvelopeWriter errorWriter,
        ModelResolver resolver,
        ModelRuntimeProviderRegistry providers,
        IExchangeRecordSink sink,
        TimeProvider timeProvider,
        ILogger<ChatCompletionGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        ArgumentNullException.ThrowIfNull(errorWriter);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.requestCodec = requestCodec;
        this.responseCodec = responseCodec;
        this.errorWriter = errorWriter;
        this.resolver = resolver;
        this.providers = providers;
        this.sink = sink;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>Forwards a completion request and returns what the client should receive.</summary>
    public async Task<ChatCompletionOutcome> CompleteAsync(
        ChatCompletionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var recorder = new ExchangeRecorder(ExchangeId.New(), request.RequestId, timeProvider);
        var acceptedAt = recorder.Now;

        recorder.Observe(ObservationType.RequestAccepted);
        recorder.Observe(ObservationType.RequestBodyRead);

        try
        {
            return await RunAsync(request, recorder, acceptedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return await CancelAsync(recorder).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception message never reaches the client. Only the fact and the correlation
            // token do (docs/SECURITY.md "Trace safety").
            logger.LogError(
                exception,
                "Completion request {RequestId} failed unexpectedly.",
                request.RequestId.Value);

            return await FailAsync(recorder, GatewayErrorCatalogue.For(FailureClass.InternalError))
                .ConfigureAwait(false);
        }
    }

    private async Task<ChatCompletionOutcome> RunAsync(
        ChatCompletionRequest request,
        ExchangeRecorder recorder,
        DateTimeOffset acceptedAt,
        CancellationToken cancellationToken)
    {
        var read = requestCodec.Read(request.Body.Span);

        if (!read.Succeeded)
        {
            // No exchange exists: CompletionExchange.Accept requires a valid model, and inventing a
            // placeholder to satisfy it would fabricate evidence (ADR 0008).
            return await FailAsync(recorder, read.Error!).ConfigureAwait(false);
        }

        var envelope = read.Envelope!;
        recorder.Observe(ObservationType.ValidationCompleted);
        recorder.Accept(envelope.Model, envelope.StreamRequested, acceptedAt);

        recorder.Update(exchange => exchange.WithRequestSummary(envelope.Summary));
        recorder.Observe(ObservationType.StructuralSummaryCreated);

        var resolution = await resolver.ResolveAsync(envelope.Model, cancellationToken).ConfigureAwait(false);

        if (!resolution.Succeeded)
        {
            return await FailAsync(recorder, Unresolved(resolution.Failure)).ConfigureAwait(false);
        }

        recorder.Observe(ObservationType.ModelResolved, resolution.Details);
        recorder.Update(exchange => exchange.Resolve(resolution.Resolution!));

        var target = resolution.Runtime!;

        if (providers.Find(target) is not { } provider)
        {
            return await FailAsync(
                recorder,
                GatewayErrorCatalogue.For(FailureClass.RuntimeNotFound),
                target.Id).ConfigureAwait(false);
        }

        var body = Forwardable(request, recorder, envelope, resolution);

        return await ForwardAsync(recorder, provider, target, body, request.RequestId, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Produces the bytes to forward, and records the routing decision when there was one.
    /// </summary>
    /// <remarks>
    /// Recording is driven by <see cref="ModelResolutionOutcome.RoutingWasApplied"/>, not by whether
    /// the body changed. An alias that selects a runtime without renaming the model, and a
    /// tie-break between two runtimes offering the same identifier, are both routing decisions the
    /// client did not make, and FR-TRACE-007 requires them to be visible even though not one byte
    /// moves.
    /// </remarks>
    private ReadOnlyMemory<byte> Forwardable(
        ChatCompletionRequest request,
        ExchangeRecorder recorder,
        ChatCompletionEnvelope envelope,
        ModelResolutionOutcome resolution)
    {
        var rewrite = resolution.RequiresBodyRewrite;

        var body = rewrite
            ? requestCodec.SubstituteModel(
                request.Body.Span,
                envelope,
                resolution.Resolution!.UpstreamModel)
            : request.Body;

        if (resolution.RoutingWasApplied)
        {
            recorder.Observe(
                ObservationType.RoutingApplied,
                Merge(resolution.Details, ("body.rewritten", rewrite ? "true" : "false")));
        }

        return body;
    }

    private async Task<ChatCompletionOutcome> ForwardAsync(
        ExchangeRecorder recorder,
        IModelRuntimeProvider provider,
        RuntimeTarget target,
        ReadOnlyMemory<byte> body,
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        recorder.Observe(
            ObservationType.UpstreamRequestOpened,
            SafeDetails.Create("runtime.id", target.Id.Value, "provider.type", target.ProviderKey));

        var result = await provider
            .CompleteAsync(
                ProviderCompletionRequest.Create(target, body, JsonMediaType, requestId),
                cancellationToken)
            .ConfigureAwait(false);

        if (result.Response is { } metadata)
        {
            recorder.Observe(ObservationType.UpstreamHeadersReceived, Describe(metadata));
            recorder.Update(exchange => exchange.WithUpstreamResponse(metadata));
        }

        if (result.FirstByteAt is not null)
        {
            recorder.Observe(ObservationType.FirstUpstreamByte);
        }

        if (result.Failure is { } failure)
        {
            // A disconnect is a cancellation, not a failure. The provider catches the cancellation
            // itself and reports it as a classified result, so without this branch the exchange
            // would be recorded as Failed and ExchangeStatus.Cancelled would be unreachable — which
            // would make a real disconnect indistinguishable from a runtime fault in both the
            // exchange list and the metrics.
            if (failure.Reason == UpstreamFailureReason.Cancelled)
            {
                return await CancelAsync(recorder).ConfigureAwait(false);
            }

            return await FailAsync(recorder, GatewayErrorCatalogue.Translate(failure), target.Id)
                .ConfigureAwait(false);
        }

        recorder.Observe(ObservationType.UpstreamCompleted);

        return await RelayAsync(recorder, result, target).ConfigureAwait(false);
    }

    /// <summary>
    /// Relays the runtime's answer verbatim, whatever status it chose.
    /// </summary>
    /// <remarks>
    /// The body is never re-encoded and the status is never rewritten. Parsing happens only to
    /// gather evidence, and a body that cannot be parsed costs a structural summary and nothing
    /// else — a runtime answering <c>429 text/plain</c> is still answering, and substituting a
    /// gateway error would discard the most actionable diagnostic a user has.
    ///
    /// The exchange therefore completes with no failure class even for a relayed 500: the transport
    /// cycle finished and AgentSplice did not fail. Success is classified from the recorded upstream
    /// status, never from the absence of a failure.
    /// </remarks>
    private async Task<ChatCompletionOutcome> RelayAsync(
        ExchangeRecorder recorder,
        ProviderCompletionResult result,
        RuntimeTarget target)
    {
        var metadata = result.Response!;
        var facts = responseCodec.Read(result.Body.Span, metadata.ContentType);

        if (facts.Summary is { } summary)
        {
            recorder.Update(exchange => exchange.WithResponseSummary(summary).WithUsage(facts.Usage));

            if (summary.NativeToolCallCount > 0)
            {
                recorder.Observe(ObservationType.NativeToolCallObserved);
            }
        }

        recorder.Complete();

        var outcome = ChatCompletionOutcome.Relayed(
            recorder,
            metadata.StatusCode,
            metadata.ContentType ?? JsonMediaType,
            result.Body,
            target.Id,
            result.RelayedHeaders);

        return await FinishAsync(recorder, outcome).ConfigureAwait(false);
    }

    /// <summary>Records a client disconnect and writes nothing back.</summary>
    /// <remarks>
    /// The socket is gone, so writing would throw and setting a status on a response that has
    /// already started would throw over the top of the real cause.
    /// </remarks>
    private async Task<ChatCompletionOutcome> CancelAsync(ExchangeRecorder recorder)
    {
        recorder.Cancel(SafeDetails.Create("cancellation.source", "client"));

        return await FinishAsync(recorder, ChatCompletionOutcome.Disconnected(recorder))
            .ConfigureAwait(false);
    }

    private async Task<ChatCompletionOutcome> FailAsync(
        ExchangeRecorder recorder,
        GatewayError gatewayError,
        RuntimeEndpointId? runtime = null)
    {
        recorder.Fail(gatewayError);

        var outcome = ChatCompletionOutcome.Failed(
            recorder,
            gatewayError,
            errorWriter.MediaType,
            errorWriter.Write(gatewayError),
            runtime);

        return await FinishAsync(recorder, outcome).ConfigureAwait(false);
    }

    /// <summary>Hands the evidence to the sink, which must never be able to fail an exchange.</summary>
    private async Task<ChatCompletionOutcome> FinishAsync(ExchangeRecorder recorder, ChatCompletionOutcome outcome)
    {
        try
        {
            // Not the request's token: the evidence for a cancelled exchange is exactly the evidence
            // worth keeping, and cancelling its recording would discard it (FR-DATA-009).
            await sink.RecordAsync(recorder.ToRecord(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                exception,
                "Recording evidence for request {RequestId} failed. The client response is unaffected.",
                recorder.RequestId.Value);
        }

        return outcome;
    }

    private static SafeDetails Describe(UpstreamResponseMetadata metadata)
    {
        var entries = new List<KeyValuePair<string, string?>>
        {
            new("upstream.status", metadata.StatusCode.ToString(System.Globalization.CultureInfo.InvariantCulture)),
            new("upstream.content_type", metadata.ContentType),
        };

        if (metadata.UpstreamRequestId is { } upstreamRequestId)
        {
            entries.Add(new KeyValuePair<string, string?>("upstream.request_id", upstreamRequestId));
        }

        return SafeDetails.Create(entries);
    }

    private static SafeDetails Merge(SafeDetails details, params (string Key, string Value)[] extra)
    {
        var entries = details.Values
            .Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value))
            .ToList();

        entries.AddRange(extra.Select(pair => new KeyValuePair<string, string?>(pair.Key, pair.Value)));

        return SafeDetails.Create(entries);
    }

    private static GatewayError Unresolved(FailureClass? failure) =>
        failure == FailureClass.RuntimeUnavailable
            ? GatewayErrorCatalogue.DiscoveryUnavailable
            : GatewayErrorCatalogue.For(FailureClass.ModelNotFound);
}
