using AgentSplice.Api.Correlation;
using AgentSplice.Application.Exchanges;
using Microsoft.AspNetCore.Http.Features;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Writes a completion's response — relayed, streamed, or the gateway's own envelope — to one HTTP
/// response, flushing every write.
/// </summary>
/// <remarks>
/// <c>DisableBuffering</c> is what stops the server coalescing small writes. Without it every claim
/// AgentSplice makes about when an event reached the client would be a claim about when it reached a
/// buffer, and the timing evidence this product exists to produce would be quietly wrong.
///
/// <c>BodyWriter.WriteAsync</c> flushes as part of the write, so there is one call per chunk and no
/// way for a caller to forget the flush.
///
/// No transport exception escapes: a client that has gone away is a returned value, because a
/// disconnect is an ordinary way for a completion to end rather than a fault to report.
/// </remarks>
internal sealed class HttpClientResponseSink : IClientResponseSink
{
    private readonly HttpContext context;

    internal HttpClientResponseSink(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        this.context = context;
    }

    /// <inheritdoc />
    public bool HasStarted => context.Response.HasStarted;

    /// <inheritdoc />
    public async ValueTask<ClientWriteResult> StartAsync(
        ClientResponseStart start,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(start);

        try
        {
            var response = context.Response;

            SetCorrelation(start.Correlation);

            foreach (var (name, value) in start.RelayedHeaders)
            {
                response.Headers[name] = value;
            }

            if (start.DisableCaching)
            {
                // AgentSplice's own decision, not the runtime's, so it is set here rather than added
                // to the relayed-header allowlist: relaying it would present a gateway decision as
                // something the runtime claimed.
                response.Headers.CacheControl = "no-cache";
            }

            response.StatusCode = start.StatusCode;
            response.ContentType = start.MediaType;

            context.Features.Get<IHttpResponseBodyFeature>()?.DisableBuffering();

            await response.StartAsync(cancellationToken).ConfigureAwait(false);

            // StartAsync commits the headers to the output pipe; Kestrel sends them only when the pipe
            // is flushed. Flushed here, so the moment this returns is the moment the client was sent
            // its status, which is what the caller records it as (ADR 0025).
            var flush = await response.BodyWriter.FlushAsync(cancellationToken).ConfigureAwait(false);

            return Outcome(flush);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            return ClientWriteResult.ClientGone;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// The flush result is the point of the call, not a return value to discard. A pipe whose reader
    /// has completed or whose flush was cancelled reports it here rather than throwing, so a sink
    /// that only watched for exceptions kept reading the runtime and kept recording bytes as
    /// delivered to a client that had stopped listening (ADR 0010).
    /// </remarks>
    public async ValueTask<ClientWriteResult> WriteAsync(
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var flush = await context.Response.BodyWriter
                .WriteAsync(bytes, cancellationToken)
                .ConfigureAwait(false);

            return Outcome(flush);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            return ClientWriteResult.ClientGone;
        }
    }

    /// <inheritdoc />
    public void Abort() => context.Abort();

    /// <summary>Whether a write that returned reached a client that is still there.</summary>
    /// <remarks>
    /// IsCompleted: the reader is finished with this pipe, so nothing written after it can reach the
    /// client. IsCanceled: the flush was cut short and these bytes are not known to have been
    /// delivered. Neither is an error, and both mean the same thing to the relay.
    ///
    /// An aborted connection is checked as well. Once Kestrel has aborted a connection it completes
    /// writes to it without sending them, and the flush can then report neither, so a client that hung
    /// up in the middle of a large buffered answer was recorded as having received all of it — the
    /// case a Linux CI run caught. Bytes written as the connection went are not known to have
    /// arrived, so the answer counts as not delivered.
    /// </remarks>
    private ClientWriteResult Outcome(System.IO.Pipelines.FlushResult flush) =>
        flush.IsCanceled || flush.IsCompleted || context.RequestAborted.IsCancellationRequested
            ? ClientWriteResult.ClientGone
            : ClientWriteResult.Written;

    private void SetCorrelation(GatewayCorrelation correlation)
    {
        var headers = context.Response.Headers;

        headers[GatewayHeaderNames.RequestId] = correlation.RequestId.Value;

        if (correlation.ExchangeId is { } exchange && !exchange.IsEmpty)
        {
            headers[GatewayHeaderNames.ExchangeId] = exchange.ToString();
        }

        if (correlation.TraceId is { } trace)
        {
            headers[GatewayHeaderNames.TraceId] = trace.Value;
        }

        if (correlation.Runtime is { } runtime)
        {
            headers[GatewayHeaderNames.Runtime] = runtime.Value;
        }
    }
}
