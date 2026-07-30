using AgentSplice.Api.Correlation;
using AgentSplice.Api.Hosting;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Exchanges;
using AgentSplice.Application.Models;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Maps the OpenAI-compatible ingress surface (docs/API.md).
/// </summary>
/// <remarks>
/// The handlers are deliberately mechanical: read the request into a value, hand it to the
/// application, write what comes back. Status selection, payload shape, and correlation are all
/// decided by the application, because CLAUDE.md forbids domain or orchestration logic in an
/// endpoint and because an endpoint is the one place none of it can be unit tested. An architecture
/// test enforces the same rule structurally.
/// </remarks>
internal static class OpenAiCompatibilityEndpoints
{
    internal static WebApplication MapOpenAiCompatibilityEndpoints(this WebApplication app)
    {
        app.MapGet("/v1/models", ListModelsAsync);

        // The concurrency bound covers completions alone. Model discovery has to stay answerable
        // exactly when the gateway is saturated, because that is when an operator most needs to ask
        // what it is doing.
        app.MapPost("/v1/chat/completions", CreateChatCompletionAsync)
            .RequireRateLimiting(CompletionConcurrencyPolicy.Name);

        return app;
    }

    private static async Task ListModelsAsync(HttpContext context, [FromServices] ModelListGateway gateway)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await gateway.ListAsync(requestId, context.RequestAborted).ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task CreateChatCompletionAsync(
        HttpContext context,
        [FromServices] ChatCompletionGateway gateway,
        [FromServices] IErrorEnvelopeWriter errorWriter,
        [FromServices] IOptions<AgentSpliceOptions> options,
        [FromServices] TimeProvider timeProvider)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);
        var cancellationToken = context.RequestAborted;

        // Read here rather than in the application: this is the first instant AgentSplice can
        // observe, and the body read that follows is a phase of its own rather than part of
        // whatever the application does first.
        var acceptedAt = timeProvider.GetUtcNow();

        var body = await RequestBodyReader
            .ReadAsync(context.Request, options.Value.Limits.MaxRequestBodyBytes, cancellationToken)
            .ConfigureAwait(false);

        var bodyReadAt = timeProvider.GetUtcNow();

        if (body.ExceededLimit)
        {
            // Refused before the application sees it, so an oversized payload never reaches the
            // parser and never reaches a runtime.
            await GatewayResponseWriter
                .WriteErrorAsync(
                    context,
                    GatewayErrorCatalogue.BodyTooLarge,
                    errorWriter,
                    requestId,
                    cancellationToken)
                .ConfigureAwait(false);

            return;
        }

        var outcome = await gateway
            .CompleteAsync(
                ChatCompletionRequest.Create(body.Body, requestId, acceptedAt, bodyReadAt),
                new HttpClientResponseSink(context),
                cancellationToken)
            .ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, outcome, cancellationToken).ConfigureAwait(false);
    }
}
