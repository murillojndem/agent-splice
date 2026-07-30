using AgentSplice.Application.Diagnostics;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using Microsoft.AspNetCore.Diagnostics;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Last resort for an exception thrown outside the application's own handling.
/// </summary>
/// <remarks>
/// The gateways catch their own faults and produce an error carrying correlation identifiers, so
/// anything reaching here escaped from the transport layer itself. The response is deliberately
/// bare: there is no request identifier to report, because the failure happened before or around the
/// code that assigns one.
///
/// It exists so that such a fault still produces the stable envelope rather than a framework error
/// page, which could disclose a stack trace.
/// </remarks>
internal sealed class GatewayExceptionHandler : IExceptionHandler
{
    private readonly IErrorEnvelopeWriter errorWriter;
    private readonly ILogger<GatewayExceptionHandler> logger;

    public GatewayExceptionHandler(
        IErrorEnvelopeWriter errorWriter,
        ILogger<GatewayExceptionHandler> logger)
    {
        ArgumentNullException.ThrowIfNull(errorWriter);
        ArgumentNullException.ThrowIfNull(logger);

        this.errorWriter = errorWriter;
        this.logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        if (httpContext.Response.HasStarted)
        {
            // The status and headers are already on the wire. Attempting to replace them would
            // throw and would hide the original fault.
            return false;
        }

        logger.LogError(
            GatewayEventIds.UnhandledPipelineFault,
            exception,
            "An unhandled fault escaped the request pipeline.");

        var error = GatewayErrorCatalogue.For(FailureClass.InternalError);

        httpContext.Response.StatusCode = error.StatusCode;
        httpContext.Response.ContentType = errorWriter.MediaType;

        await httpContext.Response.Body
            .WriteAsync(errorWriter.Write(error), cancellationToken)
            .ConfigureAwait(false);

        return true;
    }
}
