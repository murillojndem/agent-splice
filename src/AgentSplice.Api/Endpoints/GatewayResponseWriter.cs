using AgentSplice.Api.Correlation;
using AgentSplice.Application.Protocols;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Writes a <see cref="GatewayResponse"/> to the wire.
/// </summary>
/// <remarks>
/// Correlation headers are set before the body, and on every status. An error that arrived without a
/// request identifier would be the one response a user most needs to correlate and least able to.
/// </remarks>
internal static class GatewayResponseWriter
{
    internal static async Task WriteAsync(
        HttpContext context,
        GatewayResponse response,
        CancellationToken cancellationToken)
    {
        context.Response.StatusCode = response.StatusCode;
        context.Response.ContentType = response.MediaType;
        context.Response.Headers[GatewayHeaderNames.RequestId] = response.RequestId.Value;

        if (response.Runtime is { } runtime)
        {
            context.Response.Headers[GatewayHeaderNames.Runtime] = runtime.Value;
        }

        await context.Response.Body.WriteAsync(response.Body, cancellationToken).ConfigureAwait(false);
    }
}
