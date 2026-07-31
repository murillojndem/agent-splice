using AgentSplice.Api.Correlation;
using AgentSplice.Application.Exchanges;
using Microsoft.AspNetCore.Http.Features;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Writes a streamed response to one HTTP response, flushing every write.
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

            return ClientWriteResult.Written;
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

            // IsCompleted: the reader is finished with this pipe, so nothing written after it can
            // reach the client. IsCanceled: the flush was cut short and these bytes are not known to
            // have been delivered. Neither is an error, and both mean the same thing to the relay.
            return flush.IsCanceled || flush.IsCompleted
                ? ClientWriteResult.ClientGone
                : ClientWriteResult.Written;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            return ClientWriteResult.ClientGone;
        }
    }

    /// <inheritdoc />
    public void Abort() => context.Abort();

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
