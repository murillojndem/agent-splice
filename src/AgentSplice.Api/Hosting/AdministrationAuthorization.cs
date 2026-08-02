using System.Net;
using System.Security.Cryptography;
using System.Text;
using AgentSplice.Api.Correlation;
using AgentSplice.Api.Endpoints;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using Microsoft.Extensions.Options;

namespace AgentSplice.Api.Hosting;

/// <summary>
/// Decides who may read the administrative surface (FR-HEALTH-006, docs/SECURITY.md).
/// </summary>
/// <remarks>
/// One rule, chosen by whether a token is configured:
///
/// <list type="bullet">
/// <item>A token is configured — every request carries it, <em>including one arriving from
/// loopback</em>.</item>
/// <item>No token is configured — only loopback is served, and the deployment is supported only as a
/// directly-reached local process. Startup refuses a binding that is reachable from a network, so
/// this case cannot be combined with exposure by forgetting.</item>
/// </list>
///
/// The earlier rule — loopback always trusted, token required only for the rest — was wrong behind a
/// reverse proxy, which is an ordinary way to run this. Nginx or Caddy on the same host connects to
/// Kestrel from <c>127.0.0.1</c>, so every relayed request looked local and skipped the token; the
/// remote address Kestrel sees is the proxy's, and without Forwarded Headers Middleware configured
/// against known proxies there is nothing to recover the original from.
///
/// Reading <c>X-Forwarded-For</c> without that configuration would be worse than the bug: any caller
/// can send that header, so it would turn a proxy-only weakness into one anybody can reach. A trusted
/// proxy configuration is a real answer and is not this slice's; requiring the token whenever one
/// exists removes the ambiguity outright, because a request relayed by a local proxy and a request
/// made locally then have to satisfy the same check.
///
/// The comparison is fixed-time <em>for equal-length inputs</em>.
/// <see cref="CryptographicOperations.FixedTimeEquals"/> returns immediately when the lengths differ,
/// so the length of the configured token is observable. That is acceptable for a random token and is
/// stated rather than papered over.
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

        // Returned rather than written here. A filter that writes the response itself and then
        // returns null leaves the framework to materialise that null onto a response that has already
        // started, which throws over the top of the refusal.
        return new UnauthorizedEnvelope(errorWriter);
    }

    /// <summary>Whether this request may read stored evidence.</summary>
    internal bool IsAuthorized(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var expected = Token();

        // A configured token is required from everyone. See the remarks: a local reverse proxy makes
        // "arrived from loopback" and "was made locally" the same observation, so the token is the
        // only thing that separates them.
        return expected is null
            ? IsLoopback(context.Connection.RemoteIpAddress)
            : Matches(context.Request.Headers.Authorization, expected);
    }

    /// <summary>
    /// True for a connection from this machine.
    /// </summary>
    /// <remarks>
    /// A null remote address is treated as loopback: that is what an in-process test server and a
    /// Unix socket produce, and neither is a network caller. A real socket always has an address.
    ///
    /// Only consulted when no token is configured, which is the deployment that startup has already
    /// confirmed binds nowhere a network can reach.
    /// </remarks>
    private static bool IsLoopback(IPAddress? address) =>
        address is null || IPAddress.IsLoopback(address);

    /// <summary>The configured token, or <c>null</c> when this deployment has none.</summary>
    /// <remarks>
    /// Read from the environment on each call rather than cached, so rotating the variable takes
    /// effect on the next request instead of at the next restart. The read is a dictionary lookup
    /// against process environment; this surface is not a hot path.
    /// </remarks>
    private string? Token()
    {
        var variable = options.Value.Administration.ApiKeyEnvironmentVariable;

        if (string.IsNullOrWhiteSpace(variable))
        {
            return null;
        }

        var value = Environment.GetEnvironmentVariable(variable);

        // A named-but-empty variable is not a token. Treating it as one would authorise everybody
        // with an empty bearer; treating it as "no token configured" would silently downgrade a
        // deployment that asked for authentication to loopback-only. It is neither: the request is
        // refused, and startup already refuses this combination on a network binding.
        return string.IsNullOrEmpty(value) ? string.Empty : value;
    }

    /// <summary>The 401 body, written through the same path every other gateway response uses.</summary>
    private sealed class UnauthorizedEnvelope : IResult
    {
        private readonly IErrorEnvelopeWriter errorWriter;

        internal UnauthorizedEnvelope(IErrorEnvelopeWriter errorWriter) => this.errorWriter = errorWriter;

        public async Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);

            var (requestId, _) = ClientRequestId.Resolve(
                httpContext.Request.Headers[GatewayHeaderNames.ClientRequestId]);

            var error = GatewayErrorCatalogue.AdministrationUnauthorized;

            // RFC 9110 requires a 401 to say how to authenticate. No realm: a realm is a string an
            // operator would have to choose, and choosing one per deployment invites putting a
            // hostname in it.
            httpContext.Response.Headers.WWWAuthenticate = "Bearer";

            await GatewayResponseWriter
                .WriteAsync(
                    httpContext,
                    GatewayResponse.Failure(error, errorWriter.MediaType, errorWriter.Write(error), requestId),
                    httpContext.RequestAborted)
                .ConfigureAwait(false);
        }
    }

    private static bool Matches(string? authorization, string expected)
    {
        // An empty configured token authorises nobody.
        if (expected.Length == 0
            || authorization is null
            || !authorization.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var presented = Encoding.UTF8.GetBytes(authorization[BearerPrefix.Length..]);
        var configured = Encoding.UTF8.GetBytes(expected);

        return CryptographicOperations.FixedTimeEquals(presented, configured);
    }
}
