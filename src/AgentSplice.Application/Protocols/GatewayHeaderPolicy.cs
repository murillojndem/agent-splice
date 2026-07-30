using System.Collections.Frozen;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// Which headers cross the gateway, in each direction (docs/API.md, docs/SECURITY.md).
/// </summary>
/// <remarks>
/// Allowlists rather than denylists. A denylist is wrong by default: any header a runtime or a
/// client invents later crosses the boundary until someone notices, and the cost of noticing late is
/// a leaked credential or a smuggled cookie.
///
/// Lives in the application rather than in a protocol or provider module because both sides need the
/// same answer, and those two modules are forbidden from referencing each other.
/// </remarks>
public static class GatewayHeaderPolicy
{
    /// <summary>
    /// Response headers relayed from the runtime to the client.
    /// </summary>
    /// <remarks>
    /// <c>Retry-After</c> earns its place: a relayed 429 without it is not a faithful passthrough,
    /// because the one thing that status exists to convey has been discarded. Rate-limit headers are
    /// relayed for the same reason.
    ///
    /// Notably absent are <c>WWW-Authenticate</c> and <c>Set-Cookie</c>. Both describe an
    /// authentication relationship between AgentSplice and the runtime that the client is not party
    /// to and must not be invited into.
    /// </remarks>
    public static FrozenSet<string> RelayedResponseHeaders { get; } = new[]
    {
        "retry-after",
        "x-ratelimit-limit-requests",
        "x-ratelimit-limit-tokens",
        "x-ratelimit-remaining-requests",
        "x-ratelimit-remaining-tokens",
        "x-ratelimit-reset-requests",
        "x-ratelimit-reset-tokens",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Headers a proxy must never copy, in either direction.
    /// </summary>
    /// <remarks>
    /// Connection-scoped by definition (RFC 9110). Forwarding one describes the wrong connection and
    /// can desynchronise framing between the two hops.
    /// </remarks>
    public static FrozenSet<string> HopByHopHeaders { get; } = new[]
    {
        "connection",
        "keep-alive",
        "proxy-authenticate",
        "proxy-authorization",
        "te",
        "trailer",
        "transfer-encoding",
        "upgrade",
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The correlation header forwarded upstream, so a runtime log line can be tied to an exchange.
    /// </summary>
    public const string CorrelationHeader = "x-request-id";

    /// <summary>True when a response header may be relayed to the client.</summary>
    public static bool MayRelayToClient(string headerName) =>
        !string.IsNullOrWhiteSpace(headerName)
        && !HopByHopHeaders.Contains(headerName)
        && RelayedResponseHeaders.Contains(headerName);
}
