using AgentSplice.Api.Correlation;
using AgentSplice.Application.Administration;
using Microsoft.AspNetCore.Mvc;

namespace AgentSplice.Api.Endpoints;

/// <summary>
/// Maps the <c>/api/v1</c> administrative surface (docs/API.md, FR-DASH-001).
/// </summary>
/// <remarks>
/// The handlers read query values, call the application, and write what comes back. Filter
/// validation, status selection, pagination, and payload shape are all decided in
/// <see cref="ExchangeQueryService"/>, because an endpoint is the one place none of it can be unit
/// tested — and an architecture test enforces both that rule and that this project never touches a
/// <c>DbContext</c> or a JSON writer.
/// </remarks>
internal static class AdministrativeEndpoints
{
    internal static WebApplication MapAdministrativeEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/v1");

        api.MapGet("/system", GetSystem);
        api.MapGet("/runtimes", ListRuntimes);
        api.MapGet("/models", ListModelsAsync);
        api.MapGet("/health/runtimes", ListRuntimeHealthAsync);

        api.MapGet("/exchanges", ListExchangesAsync);
        api.MapGet("/exchanges/{exchangeId}", GetExchangeAsync);
        api.MapGet("/exchanges/{exchangeId}/timeline", GetTimelineAsync);
        api.MapGet("/exchanges/{exchangeId}/observations", GetObservationsAsync);

        // Outside /api/v1 on purpose: these are for an orchestrator or a container healthcheck, not
        // for a dashboard, and they answer before any administrative authorization applies. Liveness
        // in particular must never depend on a credential — a probe that fails closed would restart
        // a healthy process over a misconfigured token (FR-HEALTH-001, FR-HEALTH-002).
        app.MapGet("/health/live", GetLiveness);
        app.MapGet("/health/ready", GetReadinessAsync);

        return app;
    }

    /// <summary>
    /// Liveness: the process is running and can answer.
    /// </summary>
    /// <remarks>
    /// Touches nothing. FR-HEALTH-002 forbids depending on upstream availability, and the reason is
    /// operational rather than pedantic: a liveness probe that consulted a runtime would restart the
    /// gateway every time the model server was slow, turning a diagnosable outage into a crash loop.
    /// </remarks>
    private static IResult GetLiveness() => Results.NoContent();

    private static async Task GetReadinessAsync(
        HttpContext context,
        [FromServices] DiagnosticsService diagnostics)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await diagnostics
            .DescribeReadinessAsync(requestId, context.RequestAborted)
            .ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task GetSystem(HttpContext context, [FromServices] DiagnosticsService diagnostics)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        await GatewayResponseWriter
            .WriteAsync(context, diagnostics.Describe(requestId), context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task ListRuntimes(HttpContext context, [FromServices] DiagnosticsService diagnostics)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        await GatewayResponseWriter
            .WriteAsync(context, diagnostics.ListRuntimes(requestId), context.RequestAborted)
            .ConfigureAwait(false);
    }

    private static async Task ListModelsAsync(HttpContext context, [FromServices] DiagnosticsService diagnostics)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await diagnostics
            .ListModelsAsync(requestId, context.RequestAborted)
            .ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ListRuntimeHealthAsync(
        HttpContext context,
        [FromServices] DiagnosticsService diagnostics)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await diagnostics
            .ListRuntimeHealthAsync(requestId, context.RequestAborted)
            .ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task ListExchangesAsync(
        HttpContext context,
        [FromServices] ExchangeQueryService exchanges,
        [FromQuery] string? cursor,
        [FromQuery] int? limit,
        [FromQuery] string? status,
        [FromQuery] string? runtimeId)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await exchanges
            .ListAsync(requestId, cursor, limit, status, runtimeId, context.RequestAborted)
            .ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    private static async Task GetExchangeAsync(
        HttpContext context,
        [FromServices] ExchangeQueryService exchanges,
        string exchangeId)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await exchanges
            .FindAsync(requestId, exchangeId, context.RequestAborted)
            .ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// The timeline and the observation list are the same data on two routes.
    /// </summary>
    /// <remarks>
    /// docs/API.md describes the second as returning every recorded observation including repeated
    /// boundaries, and the first as a projection. In Stage 1 they are identical, because the timeline
    /// already is every observation in sequence order — nothing collapses or de-duplicates it. Both
    /// routes exist because both are published; serving one from the other would be a silent
    /// contract change, and diverging them now would invent a difference the evidence does not have.
    /// </remarks>
    private static Task GetTimelineAsync(
        HttpContext context,
        [FromServices] ExchangeQueryService exchanges,
        string exchangeId) =>
        GetObservationsAsync(context, exchanges, exchangeId);

    private static async Task GetObservationsAsync(
        HttpContext context,
        [FromServices] ExchangeQueryService exchanges,
        string exchangeId)
    {
        var (requestId, _) = ClientRequestId.Resolve(context.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        var response = await exchanges
            .FindObservationsAsync(requestId, exchangeId, context.RequestAborted)
            .ConfigureAwait(false);

        await GatewayResponseWriter.WriteAsync(context, response, context.RequestAborted).ConfigureAwait(false);
    }
}
