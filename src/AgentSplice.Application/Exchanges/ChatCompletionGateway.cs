using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Models;
using AgentSplice.Application.Observability;
using AgentSplice.Application.Protocols;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using AgentSplice.Domain.Measurements;
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
    private readonly ChatCompletionStreamRelay relay;
    private readonly IExchangeRecordSink sink;
    private readonly IExchangeTelemetry telemetry;
    private readonly TimeProvider timeProvider;
    private readonly ILogger<ChatCompletionGateway> logger;

    /// <summary>Creates the gateway.</summary>
    public ChatCompletionGateway(
        IChatCompletionRequestCodec requestCodec,
        IChatCompletionResponseCodec responseCodec,
        IErrorEnvelopeWriter errorWriter,
        ModelResolver resolver,
        ModelRuntimeProviderRegistry providers,
        ChatCompletionStreamRelay relay,
        IExchangeRecordSink sink,
        IExchangeTelemetry telemetry,
        TimeProvider timeProvider,
        ILogger<ChatCompletionGateway> logger)
    {
        ArgumentNullException.ThrowIfNull(requestCodec);
        ArgumentNullException.ThrowIfNull(responseCodec);
        ArgumentNullException.ThrowIfNull(errorWriter);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(providers);
        ArgumentNullException.ThrowIfNull(relay);
        ArgumentNullException.ThrowIfNull(sink);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(logger);

        this.requestCodec = requestCodec;
        this.responseCodec = responseCodec;
        this.errorWriter = errorWriter;
        this.resolver = resolver;
        this.providers = providers;
        this.relay = relay;
        this.sink = sink;
        this.telemetry = telemetry;
        this.timeProvider = timeProvider;
        this.logger = logger;
    }

    /// <summary>Forwards a completion request and returns what the client should receive.</summary>
    /// <param name="request">The request as the transport received it.</param>
    /// <param name="client">
    /// The client half of the response. A streamed answer is written through this as it arrives, so
    /// by the time an outcome exists the response is already complete on the wire.
    /// </param>
    /// <param name="cancellationToken">The client's token.</param>
    public async Task<ChatCompletionOutcome> CompleteAsync(
        ChatCompletionRequest request,
        IClientResponseSink client,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(client);

        using var trace = telemetry.StartExchange();

        var recorder = new ExchangeRecorder(ExchangeId.New(), request.RequestId, timeProvider, trace);

        // Both stamped from the transport's own readings. Taking them here instead would place both
        // boundaries after the body had already been read, making the read invisible and the
        // validation phase appear to contain it.
        recorder.Observe(ObservationType.RequestAccepted, request.AcceptedAt);
        recorder.Observe(ObservationType.RequestBodyRead, request.BodyReadAt);

        try
        {
            return await RunAsync(request, recorder, client, request.AcceptedAt, cancellationToken)
                .ConfigureAwait(false);
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
                GatewayEventIds.ExchangeFaulted,
                exception,
                "Completion request {RequestId} failed unexpectedly.",
                request.RequestId.Value);

            if (client.HasStarted)
            {
                // The status is already committed, so there is no envelope to send. Ending the
                // response politely would let a fault look like a complete answer.
                client.Abort();
            }

            return await FailAsync(recorder, GatewayErrorCatalogue.For(FailureClass.InternalError))
                .ConfigureAwait(false);
        }
    }

    private async Task<ChatCompletionOutcome> RunAsync(
        ChatCompletionRequest request,
        ExchangeRecorder recorder,
        IClientResponseSink client,
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

        return envelope.StreamRequested
            ? await StreamAsync(recorder, provider, target, body, client, request.RequestId, cancellationToken)
                .ConfigureAwait(false)
            : await ForwardAsync(recorder, provider, target, body, request.RequestId, cancellationToken)
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

    /// <summary>
    /// Opens the upstream response and relays it to the client as it arrives.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ForwardAsync"/> rather than a flag inside it, because the two differ
    /// in when the response becomes unchangeable. The buffered path can still decide to answer with
    /// an error after the runtime has replied; this one commits a status line before the first body
    /// byte exists, and everything after that has to be expressed inside the stream or by abandoning
    /// it.
    /// </remarks>
    private async Task<ChatCompletionOutcome> StreamAsync(
        ExchangeRecorder recorder,
        IModelRuntimeProvider provider,
        RuntimeTarget target,
        ReadOnlyMemory<byte> body,
        IClientResponseSink client,
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        recorder.SetRuntime(target.Id, target.ProviderKey);
        recorder.Observe(
            ObservationType.UpstreamRequestOpened,
            SafeDetails.Create("runtime.id", target.Id.Value, "provider.type", target.ProviderKey));

        var opened = await OpenStreamAsync(provider, target, body, requestId, cancellationToken)
            .ConfigureAwait(false);

        ObserveConnection(recorder, opened.Connection);

        if (opened.Response is { } metadata)
        {
            recorder.Observe(
                ObservationType.UpstreamHeadersReceived,
                metadata.HeadersReceivedAt,
                Describe(metadata, streamed: IsStream(metadata)));

            recorder.Update(exchange => exchange.WithUpstreamResponse(metadata));
        }

        if (opened.Failure is { } failure)
        {
            return failure.Reason == UpstreamFailureReason.Cancelled
                ? await CancelAsync(recorder).ConfigureAwait(false)
                : await FailAsync(recorder, GatewayErrorCatalogue.Translate(failure), target.Id)
                    .ConfigureAwait(false);
        }

        var correlation = new GatewayCorrelation(
            recorder.RequestId,
            recorder.ExchangeId,
            recorder.TraceId,
            target.Id);

        // Its own span. The provider span ends when the response is opened, so for a long generation
        // the interval the exchange actually spends transferring would otherwise be visible in no
        // span at all.
        using var streamSpan = telemetry.StartStream(target.Id, target.ProviderKey);

        var relayed = await relay
            .RelayAsync(recorder, opened, client, correlation, cancellationToken)
            .ConfigureAwait(false);

        return await CompleteRelayAsync(recorder, relayed, target).ConfigureAwait(false);
    }

    /// <summary>Opens the upstream response, timing the call as a provider request.</summary>
    /// <remarks>
    /// The provider span covers opening alone. Extending it over the relay would conflate "the
    /// runtime took a long time to answer" with "the runtime produced a long answer", which are the
    /// two things a streamed exchange most needs kept apart.
    /// </remarks>
    private async Task<ProviderStreamResult> OpenStreamAsync(
        IModelRuntimeProvider provider,
        RuntimeTarget target,
        ReadOnlyMemory<byte> body,
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        using var providerSpan = telemetry.StartProviderRequest(target.Id, target.ProviderKey);

        return await provider
            .StreamAsync(
                ProviderCompletionRequest.Create(target, body, JsonMediaType, relay.StreamMediaType, requestId),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>Records what the relay observed and produces the outcome for a written response.</summary>
    private async Task<ChatCompletionOutcome> CompleteRelayAsync(
        ExchangeRecorder recorder,
        StreamRelayOutcome relayed,
        RuntimeTarget target)
    {
        if (relayed.Summary is { } summary)
        {
            recorder.Update(exchange => exchange.WithResponseSummary(summary).WithUsage(relayed.Usage));
        }

        if (relayed.ClientGone)
        {
            return await CancelAsync(recorder).ConfigureAwait(false);
        }

        if (relayed.Error is { } error)
        {
            recorder.Fail(error, relayed.Termination);

            return await FinishAsync(
                recorder,
                ChatCompletionOutcome.Streamed(recorder, relayed.StatusCode, relayed.MediaType, target.Id, error))
                .ConfigureAwait(false);
        }

        recorder.Complete(relayed.Termination, DescribeStream(relayed));

        return await FinishAsync(
            recorder,
            ChatCompletionOutcome.Streamed(recorder, relayed.StatusCode, relayed.MediaType, target.Id, gatewayError: null))
            .ConfigureAwait(false);
    }

    /// <summary>Whether the runtime answered with an event stream, as the protocol judges it.</summary>
    /// <remarks>
    /// Asked of the relay so that one response cannot be classified two ways: this decides what the
    /// timeline says the runtime did, and the relay decides how the response is read (ADR 0010).
    /// </remarks>
    private bool IsStream(UpstreamResponseMetadata metadata) =>
        relay.MatchesStreamMediaType(metadata.RelayableContentType);

    private async Task<ChatCompletionOutcome> ForwardAsync(
        ExchangeRecorder recorder,
        IModelRuntimeProvider provider,
        RuntimeTarget target,
        ReadOnlyMemory<byte> body,
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        recorder.SetRuntime(target.Id, target.ProviderKey);
        recorder.Observe(
            ObservationType.UpstreamRequestOpened,
            SafeDetails.Create("runtime.id", target.Id.Value, "provider.type", target.ProviderKey));

        using var providerSpan = telemetry.StartProviderRequest(target.Id, target.ProviderKey);

        var result = await provider
            .CompleteAsync(
                ProviderCompletionRequest.Create(target, body, JsonMediaType, JsonMediaType, requestId),
                cancellationToken)
            .ConfigureAwait(false);

        ObserveConnection(recorder, result.Connection);

        // Stamped from the provider's readings, not from now. The provider observed each of these
        // moments as it happened; by the time control returns here the entire body has been buffered,
        // so "time to headers" measured from this point would really be time to the whole body.
        if (result.Response is { } metadata)
        {
            recorder.Observe(
                ObservationType.UpstreamHeadersReceived,
                metadata.HeadersReceivedAt,
                Describe(metadata));

            recorder.Update(exchange => exchange.WithUpstreamResponse(metadata));
        }

        if (result.FirstByteAt is { } firstByteAt)
        {
            recorder.Observe(ObservationType.FirstUpstreamByte, firstByteAt);
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

        recorder.Observe(ObservationType.UpstreamCompleted, result.CompletedAt ?? recorder.Now);

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

            // The runtime's own header, not the normalised token the evidence keeps: a client that
            // was told `charset=iso-8859-1` decodes by it, and rewriting the value would be a
            // transformation of the answer AgentSplice claims to be relaying unchanged. A header the
            // domain refused to relay falls back to the token, which says less rather than something
            // untrue.
            metadata.RelayableContentType ?? metadata.ContentType ?? JsonMediaType,
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
    /// <remarks>
    /// Runs at most once per exchange. A fault raised after the response was already written reaches
    /// the catch-all in <see cref="CompleteAsync"/>, which would otherwise produce a second outcome
    /// and a second record for the same request — two accounts of one exchange, disagreeing about
    /// how it ended.
    /// </remarks>
    private async Task<ChatCompletionOutcome> FinishAsync(ExchangeRecorder recorder, ChatCompletionOutcome outcome)
    {
        if (!recorder.TryBeginRecording())
        {
            return outcome;
        }

        Instrument(recorder, outcome);

        try
        {
            // Not the request's token: the evidence for a cancelled exchange is exactly the evidence
            // worth keeping, and cancelling its recording would discard it (FR-DATA-009).
            await sink.RecordAsync(recorder.ToRecord(), CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                GatewayEventIds.EvidenceRecordingFailed,
                exception,
                "Recording evidence for request {RequestId} failed. The client response is unaffected.",
                recorder.RequestId.Value);
        }

        return outcome;
    }

    /// <summary>
    /// Emits the span outcome and the exchange metrics.
    /// </summary>
    /// <remarks>
    /// Instrumentation must never be able to fail a request that already succeeded, so a fault here
    /// is logged and swallowed. An exchange that failed before its model was known has no
    /// <see cref="CompletionExchange"/>, and no metric is emitted for it: a dimension set with no
    /// protocol, runtime, or status would be a row that describes nothing.
    ///
    /// Every interval passes through <see cref="NonNegative"/>. Boundary timestamps are wall-clock
    /// readings, so a clock stepped backwards mid-exchange can order two boundaries impossibly. The
    /// timeline keeps that visible on purpose, and the measurement path already refuses to derive
    /// from it; a histogram is the one place where recording it anyway would quietly poison an
    /// aggregate that no one can trace back to the exchange that caused it.
    /// </remarks>
    private void Instrument(ExchangeRecorder recorder, ChatCompletionOutcome outcome)
    {
        try
        {
            if (recorder.Exchange is not { } exchange)
            {
                recorder.Trace?.SetOutcome(ExchangeStatus.Failed, outcome.Error?.Type);
                return;
            }

            recorder.Trace?.SetOutcome(exchange.Status, outcome.Error?.Type);

            var completedAt = exchange.CompletedAt ?? recorder.Now;

            if (completedAt - exchange.StartedAt is var total && total < TimeSpan.Zero)
            {
                // The clock moved backwards across this exchange. Every dimension would still be
                // valid, but the duration would not, and a histogram has no way to say "unknown".
                // One missing row is recoverable from the timeline; a negative latency sample is not.
                return;
            }

            telemetry.RecordExchange(new ExchangeTelemetrySnapshot(
                exchange.IngressProtocol,
                exchange.RuntimeEndpointId,
                recorder.ProviderKey,
                exchange.Streaming,
                exchange.Status,
                outcome.Error?.Type,
                exchange.UpstreamResponse?.StatusClass,
                total,
                NonNegative(UpstreamDuration(recorder)),
                NonNegative(TimeToHeaders(recorder)),
                exchange.Usage.PromptTokens,
                exchange.Usage.CompletionTokens)
            {
                TimeToFirstByte = NonNegative(recorder.DurationBetween(
                    ObservationType.UpstreamRequestOpened,
                    ObservationType.FirstUpstreamByte)),

                // Present only for an exchange that actually streamed, which is also what tells the
                // metric layer to attach the termination dimension at all.
                StreamTermination = exchange.StreamedResponse ? exchange.StreamTermination : null,
                StreamEvents = exchange.StreamedResponse ? exchange.ResponseSummary?.StreamEventCount : null,
                StreamBytes = exchange.StreamedResponse ? exchange.ResponseSummary?.ResponseBodyBytes : null,
                TimeToFirstSemanticEvent = NonNegative(recorder.DurationBetween(
                    ObservationType.RequestAccepted,
                    ObservationType.FirstSemanticEvent)),
                TimeToFirstClientEvent = NonNegative(recorder.DurationBetween(
                    ObservationType.RequestAccepted,
                    ObservationType.FirstClientEventFlushed)),
                GenerationThroughput = recorder.FindMeasurement(MeasurementNames.GenerationThroughput),
            });
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(
                GatewayEventIds.InstrumentationFailed,
                exception,
                "Instrumenting request {RequestId} failed. The client response is unaffected.",
                recorder.RequestId.Value);
        }
    }

    /// <summary>
    /// Records what establishing a connection cost, when this request had to establish one.
    /// </summary>
    /// <remarks>
    /// Appended before the headers boundary so the timeline stays in chronological order even though
    /// the provider only reports the timing after the response arrives. A request served by a pooled
    /// connection records nothing: no connection was established, which is not the same as one that
    /// took no time.
    /// </remarks>
    private static void ObserveConnection(ExchangeRecorder recorder, UpstreamConnectObservation? connection)
    {
        if (connection is not { } established)
        {
            return;
        }

        recorder.Observe(ObservationType.UpstreamConnectionStarted, established.StartedAt);
        recorder.Observe(ObservationType.UpstreamConnectionEstablished, established.EstablishedAt);
    }

    /// <summary>Drops an interval whose boundaries are ordered impossibly, rather than clamping it.</summary>
    private static TimeSpan? NonNegative(TimeSpan? value) =>
        value is { } elapsed && elapsed >= TimeSpan.Zero ? elapsed : null;

    private static TimeSpan? UpstreamDuration(ExchangeRecorder recorder) =>
        recorder.DurationBetween(ObservationType.UpstreamRequestOpened, ObservationType.UpstreamCompleted);

    private static TimeSpan? TimeToHeaders(ExchangeRecorder recorder) =>
        recorder.DurationBetween(ObservationType.UpstreamRequestOpened, ObservationType.UpstreamHeadersReceived);

    private static SafeDetails Describe(UpstreamResponseMetadata metadata, bool? streamed = null)
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

        // Recorded only where the question was asked. A runtime that answers a streaming request
        // with an ordinary body has not failed, but it has done something the client did not ask
        // for, and nothing else in the record would show it.
        if (streamed is { } wasStream)
        {
            entries.Add(new KeyValuePair<string, string?>("upstream.streamed", wasStream ? "true" : "false"));
        }

        return SafeDetails.Create(entries);
    }

    /// <summary>
    /// Records the facts about a stream's ending that its termination value cannot hold.
    /// </summary>
    /// <remarks>
    /// A stream can carry a malformed event and still end with the protocol's terminator. The
    /// termination enum names the anomaly, because that is the more useful of the two; these details
    /// keep the rest rather than discarding it.
    /// </remarks>
    private static SafeDetails DescribeStream(StreamRelayOutcome relayed)
    {
        if (!relayed.Streamed)
        {
            return SafeDetails.Empty;
        }

        var entries = new List<KeyValuePair<string, string?>>
        {
            new("stream.terminator", relayed.ProtocolTerminatorObserved ? "observed" : "absent"),
            new("stream.events", relayed.ClientEvents.ToString(System.Globalization.CultureInfo.InvariantCulture)),
        };

        if (relayed.IncompleteEventBytes > 0)
        {
            entries.Add(new KeyValuePair<string, string?>(
                "stream.incomplete_event_bytes",
                relayed.IncompleteEventBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)));
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
