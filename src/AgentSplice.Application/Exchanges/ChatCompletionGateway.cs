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
/// The Stage 1 completion request path (docs/ARCHITECTURE.md "Stage 1 primary request flow").
/// </summary>
/// <remarks>
/// The whole of the completion path lives here rather than in an endpoint, because an endpoint is
/// the one place none of it can be unit tested.
///
/// The order of the first steps is deliberate: the structural summary is built <em>before</em>
/// resolution, so a request that names an unknown model still leaves safe evidence of what arrived.
/// Building it afterwards would lose that evidence in precisely the case an operator most needs it.
///
/// Every response is written here, through <see cref="IClientResponseSink"/>, and the exchange is
/// recorded only after the write returned. Recording first dated the client's completion before the
/// client had been sent anything, and recorded an error code for an envelope that might never be
/// written (ADR 0025).
/// </remarks>
public sealed class ChatCompletionGateway
{
    private const string JsonMediaType = "application/json";

    private static readonly Dictionary<string, string> NoHeaders = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Forwards a completion request and writes the client's response.</summary>
    /// <param name="request">The request as the transport received it.</param>
    /// <param name="client">
    /// The client half of the response. Everything the client receives is written through this — a
    /// relayed answer as it arrives, or the gateway's own error envelope — so when this returns the
    /// response is complete on the wire and the transport adds nothing.
    /// </param>
    /// <param name="cancellationToken">The client's token.</param>
    public async Task CompleteAsync(
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
            await RunAsync(request, recorder, client, request.AcceptedAt, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await CancelAsync(recorder).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // The exception message never reaches the client. Only the fact and the correlation
            // token do (docs/SECURITY.md "Trace safety").
            logger.LogError(
                GatewayEventIds.ExchangeFaulted,
                exception,
                "Completion exchange {ExchangeId} failed unexpectedly.",
                recorder.ExchangeId.Value);

            var internalError = GatewayErrorCatalogue.For(FailureClass.InternalError);

            if (client.HasStarted)
            {
                // The status is already committed, so there is no envelope to send and none to
                // record. Ending the response politely would let a fault look like a complete answer,
                // and writing an envelope now would throw over the top of the real cause.
                client.Abort();

                await AbandonAsync(recorder, internalError, AbandonedTermination(recorder))
                    .ConfigureAwait(false);

                return;
            }

            await FailAsync(recorder, client, internalError, runtime: null, termination: null, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async Task RunAsync(
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
            await FailAsync(recorder, client, read.Error!, runtime: null, termination: null, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var envelope = read.Envelope!;
        recorder.Observe(ObservationType.ValidationCompleted);
        recorder.Accept(envelope.Model, envelope.StreamRequested, acceptedAt);

        recorder.Update(exchange => exchange.WithRequestSummary(envelope.Summary));
        recorder.Observe(ObservationType.StructuralSummaryCreated);

        var resolution = await resolver.ResolveAsync(envelope.Model, cancellationToken).ConfigureAwait(false);

        if (!resolution.Succeeded)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                // The client left while its model was being looked up, and the lookup it abandoned
                // says nothing about the runtime. Recording it as an outage would blame a runtime for
                // a disconnect, and an envelope written now would reach nobody.
                await CancelAsync(recorder).ConfigureAwait(false);
                return;
            }

            await FailAsync(recorder, client, Unresolved(resolution.Failure), runtime: null, termination: null, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        recorder.Observe(ObservationType.ModelResolved, resolution.Details);
        recorder.Update(exchange => exchange.Resolve(resolution.Resolution!));

        var target = resolution.Runtime!;

        if (providers.Find(target) is not { } provider)
        {
            await FailAsync(
                recorder,
                client,
                GatewayErrorCatalogue.For(FailureClass.RuntimeNotFound),
                target.Id,
                termination: null,
                cancellationToken).ConfigureAwait(false);

            return;
        }

        var body = Forwardable(request, recorder, envelope, resolution);

        if (envelope.StreamRequested)
        {
            await StreamAsync(recorder, provider, target, body, client, request.RequestId, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            await ForwardAsync(recorder, provider, target, body, client, request.RequestId, cancellationToken)
                .ConfigureAwait(false);
        }
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
    /// an error after the runtime has finished; this one commits the client's status with the
    /// runtime's first body byte, and everything after that has to be expressed inside the stream or
    /// by abandoning it.
    /// </remarks>
    private async Task StreamAsync(
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
            if (failure.Reason == UpstreamFailureReason.Cancelled)
            {
                await CancelAsync(recorder).ConfigureAwait(false);
                return;
            }

            recorder.ObserveUpstreamFailure(failure);

            await FailAsync(recorder, client, GatewayErrorCatalogue.Translate(failure), target.Id, termination: null, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        // Its own span. The provider span ends when the response is opened, so for a long generation
        // the interval the exchange actually spends transferring would otherwise be visible in no
        // span at all.
        using var streamSpan = telemetry.StartStream(target.Id, target.ProviderKey);

        var relayed = await relay
            .RelayAsync(recorder, opened, client, Correlation(recorder, target.Id), cancellationToken)
            .ConfigureAwait(false);

        await CompleteRelayAsync(recorder, client, relayed, target, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Records how a relay ended, answering with an envelope if the client still can be.</summary>
    /// <remarks>
    /// A failure before the runtime's first byte reaches a client whose response never started, so
    /// its status is still the gateway's to choose and the stable error goes out with every
    /// correlation header. After that byte the response can only be abandoned, and the exchange
    /// records why it failed and no code (ADR 0025).
    /// </remarks>
    private async Task CompleteRelayAsync(
        ExchangeRecorder recorder,
        IClientResponseSink client,
        StreamRelayOutcome relayed,
        RuntimeTarget target,
        CancellationToken cancellationToken)
    {
        if (relayed.Summary is { } summary)
        {
            recorder.Update(exchange => exchange.WithResponseSummary(summary).WithUsage(relayed.Usage));
        }

        if (relayed.ResponseStarted)
        {
            recorder.RecordRelayedToClient(relayed.ClientBytes);
        }

        if (relayed.ClientGone)
        {
            await CancelAsync(recorder).ConfigureAwait(false);
            return;
        }

        if (relayed.Error is { } error)
        {
            if (relayed.ResponseStarted)
            {
                await AbandonAsync(recorder, error, relayed.Termination).ConfigureAwait(false);
                return;
            }

            await FailAsync(recorder, client, error, target.Id, relayed.Termination, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        recorder.Complete(relayed.Termination, DescribeStream(relayed));

        await FinishAsync(recorder).ConfigureAwait(false);
    }

    /// <summary>Whether the runtime answered with an event stream, as the protocol judges it.</summary>
    /// <remarks>
    /// Asked of the relay so that one response cannot be classified two ways: this decides what the
    /// timeline says the runtime did, and the relay decides how the response is read (ADR 0010).
    /// </remarks>
    private bool IsStream(UpstreamResponseMetadata metadata) =>
        relay.MatchesStreamMediaType(metadata.ParsedMediaType);

    private async Task ForwardAsync(
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

        ProviderCompletionResult result;

        using (telemetry.StartProviderRequest(target.Id, target.ProviderKey))
        {
            result = await provider
                .CompleteAsync(
                    ProviderCompletionRequest.Create(target, body, JsonMediaType, JsonMediaType, requestId),
                    cancellationToken)
                .ConfigureAwait(false);
        }

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
                await CancelAsync(recorder).ConfigureAwait(false);
                return;
            }

            recorder.ObserveUpstreamFailure(failure);

            await FailAsync(recorder, client, GatewayErrorCatalogue.Translate(failure), target.Id, termination: null, cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        recorder.Observe(ObservationType.UpstreamCompleted, result.CompletedAt ?? recorder.Now);

        await RelayAsync(recorder, client, result, target, cancellationToken).ConfigureAwait(false);
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
    ///
    /// It completes only once the write has returned. A client that leaves during it was given part
    /// of an answer at most, so the exchange is the cancellation it was, and records no byte count,
    /// because how much reached the client is not known.
    /// </remarks>
    private async Task RelayAsync(
        ExchangeRecorder recorder,
        IClientResponseSink client,
        ProviderCompletionResult result,
        RuntimeTarget target,
        CancellationToken cancellationToken)
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

        var start = new ClientResponseStart(
            metadata.StatusCode,

            // The runtime's own header, not the normalised token the evidence keeps: a client that
            // was told `charset=iso-8859-1` decodes by it, and rewriting the value would be a
            // transformation of the answer AgentSplice claims to be relaying unchanged. A header the
            // domain refused to relay falls back to the token, which says less rather than something
            // untrue.
            metadata.RelayableContentType ?? metadata.ContentType ?? JsonMediaType,
            Correlation(recorder, target.Id),
            result.RelayedHeaders,
            DisableCaching: false);

        if (!await WriteResponseAsync(recorder, client, start, result.Body, cancellationToken).ConfigureAwait(false))
        {
            await CancelAsync(recorder).ConfigureAwait(false);
            return;
        }

        recorder.RecordRelayedToClient(result.Body.Length);
        recorder.Complete();

        await FinishAsync(recorder).ConfigureAwait(false);
    }

    /// <summary>Records a client disconnect and writes nothing back.</summary>
    /// <remarks>
    /// The socket is gone, so writing would throw and setting a status on a response that has
    /// already started would throw over the top of the real cause.
    /// </remarks>
    private async Task CancelAsync(ExchangeRecorder recorder)
    {
        recorder.Cancel(SafeDetails.Create("cancellation.source", "client"));

        await FinishAsync(recorder).ConfigureAwait(false);
    }

    /// <summary>Records a failure whose started response was abandoned; nothing more is written.</summary>
    private async Task AbandonAsync(ExchangeRecorder recorder, GatewayError failure, StreamTermination termination)
    {
        recorder.Abandon(failure, termination);

        await FinishAsync(recorder).ConfigureAwait(false);
    }

    /// <summary>Writes the gateway's error envelope, then records the failure it reported.</summary>
    /// <remarks>
    /// The error is recorded only once its envelope was written, because the record's error code is
    /// the code the client received. A client that left before it could be told received nothing, so
    /// the exchange ends as the cancellation it became, and the boundary that recorded what had gone
    /// wrong upstream stays on its timeline.
    /// </remarks>
    private async Task FailAsync(
        ExchangeRecorder recorder,
        IClientResponseSink client,
        GatewayError gatewayError,
        RuntimeEndpointId? runtime,
        StreamTermination? termination,
        CancellationToken cancellationToken)
    {
        var start = new ClientResponseStart(
            gatewayError.StatusCode,
            errorWriter.MediaType,
            Correlation(recorder, runtime),
            NoHeaders,
            DisableCaching: false);

        if (!await WriteResponseAsync(recorder, client, start, errorWriter.Write(gatewayError), cancellationToken)
                .ConfigureAwait(false))
        {
            await CancelAsync(recorder).ConfigureAwait(false);
            return;
        }

        recorder.Fail(gatewayError, termination);

        await FinishAsync(recorder).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes one whole response, returning <c>false</c> when the client is gone before it ends.
    /// </summary>
    /// <remarks>
    /// The commit is a boundary of its own, stamped when the headers were flushed, so that every
    /// response's timeline says when the client was sent its status — the moment ADR 0019 found
    /// invisible.
    /// </remarks>
    private static async Task<bool> WriteResponseAsync(
        ExchangeRecorder recorder,
        IClientResponseSink client,
        ClientResponseStart start,
        ReadOnlyMemory<byte> body,
        CancellationToken cancellationToken)
    {
        if (await client.StartAsync(start, cancellationToken).ConfigureAwait(false) is ClientWriteResult.ClientGone)
        {
            return false;
        }

        recorder.Observe(ObservationType.ClientResponseStarted);

        return body.IsEmpty
            || await client.WriteAsync(body, cancellationToken).ConfigureAwait(false) is ClientWriteResult.Written;
    }

    /// <summary>Hands the evidence to the sink, which must never be able to fail an exchange.</summary>
    /// <remarks>
    /// Runs at most once per exchange. A fault raised after the response was already written reaches
    /// the catch-all in <see cref="CompleteAsync"/>, which would otherwise produce a second record
    /// for the same request — two accounts of one exchange, disagreeing about how it ended.
    /// </remarks>
    private async Task FinishAsync(ExchangeRecorder recorder)
    {
        if (!recorder.TryBeginRecording())
        {
            return;
        }

        Instrument(recorder);

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
                "Recording evidence for exchange {ExchangeId} failed. The client response is unaffected.",
                recorder.ExchangeId.Value);
        }
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
    /// The error type is the failure's, whether or not its envelope reached the client: a response
    /// abandoned mid-stream failed as surely as one answered with a 504, and only the record's error
    /// code depends on delivery.
    ///
    /// Every interval passes through <see cref="NonNegative"/>. Boundary timestamps are wall-clock
    /// readings, so a clock stepped backwards mid-exchange can order two boundaries impossibly. The
    /// timeline keeps that visible on purpose, and the measurement path already refuses to derive
    /// from it; a histogram is the one place where recording it anyway would quietly poison an
    /// aggregate that no one can trace back to the exchange that caused it.
    /// </remarks>
    private void Instrument(ExchangeRecorder recorder)
    {
        try
        {
            var errorType = recorder.Failure?.Type;

            if (recorder.Exchange is not { } exchange)
            {
                // The store classifies such a request from its timeline, and so does its span: a
                // client that left before its model was known cancelled, and did not fail.
                recorder.Trace?.SetOutcome(
                    recorder.HasObserved(ObservationType.ClientCancelled) ? ExchangeStatus.Cancelled : ExchangeStatus.Failed,
                    errorType);

                return;
            }

            recorder.Trace?.SetOutcome(exchange.Status, errorType);

            var completedAt = exchange.CompletedAt ?? recorder.Now;

            if (completedAt - exchange.StartedAt is var total && total < TimeSpan.Zero)
            {
                // The clock moved backwards across this exchange. Every dimension would still be
                // valid, but the duration would not, and a histogram has no way to say "unknown".
                // One missing row is recoverable from the timeline; a negative latency sample is not.
                return;
            }

            // The stream's size is what reached the client. A stream that failed before its first
            // byte was answered with an envelope instead, and contributes no data point rather than
            // a zero that would read as a stream the client was sent and that carried nothing.
            var relayedBytes = exchange.StreamedResponse ? recorder.RelayedClientBytes : null;

            telemetry.RecordExchange(new ExchangeTelemetrySnapshot(
                exchange.IngressProtocol,
                exchange.RuntimeEndpointId,
                recorder.ProviderKey,
                exchange.Streaming,
                exchange.Status,
                errorType,
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
                StreamEvents = relayedBytes is null ? null : exchange.ResponseSummary?.StreamEventCount,
                StreamBytes = relayedBytes,
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
                "Instrumenting exchange {ExchangeId} failed. The client response is unaffected.",
                recorder.ExchangeId.Value);
        }
    }

    /// <summary>How a response abandoned by the catch-all ended, in the domain's terms.</summary>
    /// <remarks>
    /// An exchange that streamed must record how its stream ended, and a fault is not a way AgentSplice
    /// can classify; one that never streamed has no stream to describe.
    /// </remarks>
    private static StreamTermination AbandonedTermination(ExchangeRecorder recorder) =>
        recorder.Exchange?.StreamedResponse == true ? StreamTermination.Unknown : StreamTermination.NotApplicable;

    private static GatewayCorrelation Correlation(ExchangeRecorder recorder, RuntimeEndpointId? runtime) =>
        new(recorder.RequestId, recorder.ExchangeId, recorder.TraceId, runtime);

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
