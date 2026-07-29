using AgentSplice.Api.Correlation;
using AgentSplice.Application.Models;
using Microsoft.AspNetCore.Mvc;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Maps the OpenAI-compatible ingress surface (docs/API.md).
/// </summary>
/// <remarks>
/// The handlers are deliberately mechanical: read the request into a value, hand it to the
/// application, write what comes back. Status selection, payload shape, and correlation are all
/// decided by the application, because CLAUDE.md forbids domain or orchestration logic in an
/// endpoint and because an endpoint is the one place none of it can be unit tested.
/// </remarks>
internal static class OpenAiCompatibilityEndpoints
{
    internal static WebApplication MapOpenAiCompatibilityEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/models", ListModelsAsync);

        return app;
    }

    private static async Task ListModelsAsync(HttpContext context, [FromServices] ModelListGateway gateway)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await gateway.ListAsync(requestId, context.RequestAborted).ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }
}
