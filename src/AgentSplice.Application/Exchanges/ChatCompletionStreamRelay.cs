using AgentSplice.Application.Configuration;
using AgentSplice.Application.Protocols;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Exchanges;
using Microsoft.Extensions.Options;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// Relays an upstream response to the client byte for byte, observing it as it passes
/// (docs/SPECIFICATION.md FR-STR-001 to FR-STR-012).
/// </summary>
/// <remarks>
/// The order inside the pump is the design. Read, write and flush, and only then decode: nothing
/// AgentSplice does for its own evidence sits between an upstream byte arriving and that byte
/// reaching the client. That makes bounded flush delay (FR-STR-003) a property of the structure
/// rather than a promise about how fast the decoder is.
///
/// Each of those steps takes its own clock reading, and the boundary it produces is recorded from
/// that reading alone. Sharing one timestamp across the read, the flush, the decode, and the
/// semantic classification is cheaper and was how this began; it also made four distinct latencies
/// indistinguishable from zero (ADR 0010).
///
/// Relaying raw bytes also makes valid SSE free rather than earned. Chunk boundaries are not event
/// boundaries, a conforming client buffers until the blank line, and no re-encoding can normalise an
/// escape sequence or a number AgentSplice was never asked to touch.
/// </remarks>
public sealed class ChatCompletionStreamRelay
{
    private readonly IStreamEventInterpreter interpreter;
    private readonly IChatCompletionResponseCodec responseCodec;
    private readonly IOptions<AgentSpliceOptions> options;
    private readonly TimeProvider timeProvider;

    /// <summary>Creates the relay.</summary>
    public ChatCompletionStreamRelay(
        IStreamEventInterpreter interpreter,
        IChatCompletionResponseCodec responseCodec,
        IOptions<AgentSpliceOptions> options,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(interpreter);
        ArgumentNullException.ThrowIfNull(responseCodec);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);

        this.interpreter = interpreter;
        this.responseCodec = responseCodec;
        this.options = options;
        this.timeProvider = timeProvider;
    }

    /// <summary>The media type this gateway's streamed responses use.</summary>
    public string StreamMediaType => interpreter.StreamMediaType;

    /// <summary>True when a runtime answered with this protocol's streamed media type.</summary>
    /// <remarks>
    /// Delegated to the protocol so the orchestrator and the relay cannot classify one response two
    /// ways. Before this existed both compared the header for equality against a literal, which
    /// reads a conforming <c>text/event-stream; charset=utf-8</c> as a buffered answer (ADR 0010).
    /// </remarks>
    public bool MatchesStreamMediaType(string? contentType) =>
        interpreter.MatchesStreamMediaType(contentType);

    /// <summary>Relays an opened upstream response to the client.</summary>
    public async Task<StreamRelayOutcome> RelayAsync(
        ExchangeRecorder recorder,
        ProviderStreamResult opened,
        IClientResponseSink client,
        GatewayCorrelation correlation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(recorder);
        ArgumentNullException.ThrowIfNull(opened);
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(correlation);

        var metadata = opened.Response!;
        var upstream = opened.Body!;

        // The runtime's own header, parameters and all. Classification below reads the same value
        // through the protocol's matcher; neither rewrites it, because what the client receives must
        // be what the runtime said (ADR 0010). The normalised token is the fallback for a header the
        // domain refused to relay, which is a narrower answer rather than a wrong one.
        var mediaType = metadata.RelayableContentType
            ?? metadata.ContentType
            ?? StreamRelayPump.FallbackMediaType;

        // The runtime decides whether this is a stream, not the request. A runtime that answers a
        // streaming request with an ordinary body is answering, and relaying that verbatim is the
        // same rule the buffered path follows for a status it did not expect.
        var streamed = interpreter.MatchesStreamMediaType(metadata.RelayableContentType);

        await using (upstream.ConfigureAwait(false))
        {
            var start = new ClientResponseStart(
                metadata.StatusCode,
                mediaType,
                correlation,
                opened.RelayedHeaders,
                DisableCaching: streamed);

            if (await client.StartAsync(start, cancellationToken).ConfigureAwait(false) is ClientWriteResult.ClientGone)
            {
                // Nothing was committed, so this is an ordinary cancellation rather than a stream
                // that ended badly.
                return new StreamRelayOutcome
                {
                    Termination = StreamTermination.NotApplicable,
                    StatusCode = metadata.StatusCode,
                    MediaType = mediaType,
                    ClientGone = true,
                };
            }

            if (streamed)
            {
                // From here the status line is on the wire and can no longer be changed, which is
                // the fact every remaining decision turns on.
                recorder.BeginStreaming();
            }

            var pump = new StreamRelayPump(
                recorder,
                upstream,
                client,
                streamed ? interpreter.Begin() : null,
                responseCodec,
                options.Value.Limits,
                timeProvider,
                metadata,
                mediaType,
                streamed);

            return await pump.RunAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
