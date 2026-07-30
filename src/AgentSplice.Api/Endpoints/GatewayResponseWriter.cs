using AgentSplice.Api.Correlation;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Writes an application-decided response to the wire.
/// </summary>
/// <remarks>
/// Correlation headers are set before the body and on every status, including errors. An error
/// arriving without a request identifier would be the one response a user most needs to correlate
/// and least able to.
/// </remarks>
internal static class GatewayResponseWriter
{
    internal static async Task WriteAsync(
        HttpContext context,
        GatewayResponse response,
        CancellationToken cancellationToken)
    {
        SetCorrelation(context, response.RequestId, exchangeId: null, response.Runtime);

        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.MediaType;

        await context.Response.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }

    internal static async Task WriteAsync(
        HttpContext context,
        ChatCompletionOutcome outcome,
        CancellationToken cancellationToken)
    {
        if (outcome.ClientDisconnected)
        {
            // The socket is gone. Writing would throw, and setting a status on a response that has
            // already started would throw over the top of the real cause.
            return;
        }

        SetCorrelation(context, outcome.RequestId, outcome.ExchangeId, outcome.Runtime);

        foreach (var (name, value) in outcome.RelayedHeaders)
        {
            context.Response.Headers[name] = value;
        }

        context.Response.StatusCode = outcome.StatusCode;
        context.Response.ContentType = outcome.MediaType;

        await context.Response.Body.WriteAsync(outcome.Body, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Writes an error raised at the transport boundary, before the application ran.</summary>
    internal static async Task WriteErrorAsync(
        HttpContext context,
        GatewayError gatewayError,
        IErrorEnvelopeWriter errorWriter,
        PublicRequestId requestId,
        CancellationToken cancellationToken)
    {
        SetCorrelation(context, requestId, exchangeId: null, runtime: null);

        context.Response.StatusCode = gatewayError.StatusCode;
        context.Response.ContentType = errorWriter.MediaType;

        await context.Response.Body
            .WriteAsync(errorWriter.Write(gatewayError), cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Sets the correlation headers a response is entitled to.
    /// </summary>
    /// <remarks>
    /// Each header is present only when the thing it names exists. Model discovery returns no
    /// exchange identifier because it is not an exchange, and a request that failed before routing
    /// returns no runtime, because naming a record or a runtime that was never involved would be a
    /// fabrication.
    /// </remarks>
    private static void SetCorrelation(
        HttpContext context,
        PublicRequestId requestId,
        ExchangeId? exchangeId,
        RuntimeEndpointId? runtime)
    {
        context.Response.Headers[GatewayHeaderNames.RequestId] = requestId.Value;

        if (exchangeId is { } exchange && !exchange.IsEmpty)
        {
            context.Response.Headers[GatewayHeaderNames.ExchangeId] = exchange.ToString();
        }

        if (runtime is { } resolved)
        {
            context.Response.Headers[GatewayHeaderNames.Runtime] = resolved.Value;
        }
    }
}
