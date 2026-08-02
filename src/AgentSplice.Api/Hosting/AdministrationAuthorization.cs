using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using AgentSplice.Api.Correlation;
using AgentSplice.Api.Endpoints;
using Microsoft.Extensions.Options;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Decides who may read the administrative surface (FR-HEALTH-006).
/// </summary>
/// <remarks>
/// Two rules, and the second is the one that matters. A request arriving from a loopback address is
/// allowed: it came from this machine, which is the deployment AgentSplice is built for, and
/// requiring a token there would make the ordinary local case need secret management to look at its
/// own traces. A request arriving from anywhere else must carry the configured bearer token.
///
/// Startup validation refuses a non-loopback binding with no token configured, so the dangerous
/// combination cannot be reached by forgetting rather than by deciding. The per-request check is
/// still the authority, because a binding is what an operator configured and a remote address is what
/// actually arrived.
///
/// The comparison is constant-time. A token check that returns early on the first wrong byte tells an
/// attacker how much of a guess was right, one request at a time.
/// </remarks>
internal sealed class AdministrationAuthorization : IEndpointFilter
{
    private const string BearerPrefix = "Bearer ";

    private readonly IOptions<AgentSpliceOptions> options;
    private readonly IErrorEnvelopeWriter errorWriter;

    public AdministrationAuthorization(
        IOptions<AgentSpliceOptions> options,
        IErrorEnvelopeWriter errorWriter)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(errorWriter);

        this.options = options;
        this.errorWriter = errorWriter;
    }

    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        if (IsAuthorized(context.HttpContext))
        {
            return await next(context).ConfigureAwait(false);
        }

        var (requestId, _) = ClientRequestId.Resolve(
            context.HttpContext.Request.Headers[GatewayHeaderNames.ClientRequestId]);

        await GatewayResponseWriter
            .WriteAsync(
                context.HttpContext,
                GatewayResponse.Failure(
                    GatewayErrorCatalogue.AdministrationUnauthorized,
                    errorWriter.MediaType,
                    errorWriter.Write(GatewayErrorCatalogue.AdministrationUnauthorized),
                    requestId),
                context.HttpContext.RequestAborted)
            .ConfigureAwait(false);

        return null;
    }

    private bool IsAuthorized(HttpContext context)
    {
        if (IsLoopback(context.Connection.RemoteIpAddress))
        {
            return true;
        }

        var expected = Token();

        // No token configured and a remote caller: refused. Startup validation should already have
        // stopped this deployment, and if it somehow starts, the safe answer is the closed one.
        return expected is not null && Matches(context.Request.Headers.Authorization, expected);
    }

    /// <summary>
    /// True for a connection from this machine.
    /// </summary>
    /// <remarks>
    /// A null remote address is treated as loopback: that is what an in-process test server and a
    /// Unix socket produce, and neither is a network caller. It is not a hole a remote client can
    /// reach through, because a real socket always has an address.
    /// </remarks>
    private static bool IsLoopback(IPAddress? address) =>
        address is null || IPAddress.IsLoopback(address);

    private string? Token()
    {
        var variable = options.Value.Administration.ApiKeyEnvironmentVariable;

        if (string.IsNullOrWhiteSpace(variable))
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable(variable);

        return string.IsNullOrEmpty(value) ? null : value;
    }

    private static bool Matches(string? authorization, string expected)
    {
        if (authorization is null || !authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorization[BearerPrefix.Length..]);
        var configured = Encoding.UTF8.GetBytes(expected);

        // FixedTimeEquals returns false for a length mismatch without comparing, so the lengths are
        // compared first only to avoid the exception, not to shortcut the comparison.
        return presented.Length == configured.Length
            && CryptographicOperations.FixedTimeEquals(presented, configured);
    }
}
