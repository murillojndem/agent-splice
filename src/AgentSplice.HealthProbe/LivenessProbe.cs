using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace AgentSplice.HealthProbe;

/// <summary>
/// One <c>GET</c> against the gateway's liveness endpoint, reported as a Docker health-check exit
/// code (ADR 0016, FR-HEALTH-001, FR-HEALTH-002).
/// </summary>
/// <remarks>
/// Liveness rather than readiness: <c>/health/live</c> touches no runtime and no database, so a slow
/// or absent model server can never mark the gateway unhealthy. Readiness may legitimately answer
/// 503 while LM Studio is down, and a container reported unhealthy for that would be reporting the
/// runtime's outage as the gateway's.
///
/// <c>GET</c> rather than <c>HEAD</c>, because the endpoint answers <c>HEAD</c> with 405, and any 2xx
/// counts as live: the endpoint answers 204, and a probe that demanded exactly 200 would fail
/// against a correct gateway.
///
/// The target must be an <c>http</c> URL whose host is a loopback IP literal. The probe asks this
/// container about itself and nothing else, so <c>localhost</c> is refused along with every other
/// name: resolving it would make the answer depend on the container's resolver configuration. It
/// never uses a proxy and never follows a redirect, and it reads the status line and headers only —
/// the body is never read.
///
/// Every outcome is one line on standard output, success included: Docker keeps the last few
/// results in <c>docker inspect</c>, and the elapsed time is worth having there. The line names the
/// status or the failure class and never echoes a target it refused.
/// </remarks>
internal static class LivenessProbe
{
    /// <summary>Docker's "healthy".</summary>
    internal const int Healthy = 0;

    /// <summary>
    /// Docker's "unhealthy", and the only other code the probe returns: Docker reserves 2.
    /// </summary>
    internal const int Unhealthy = 1;

    private const string RefusedTarget =
        "unhealthy: the probe takes one argument, an http URL on a loopback IP address";

    /// <summary>How long the probe waits for the response headers before reporting no answer.</summary>
    /// <remarks>
    /// Shorter than the <c>HEALTHCHECK --timeout</c>, so that the probe reports why it failed rather
    /// than being killed by Docker with nothing said; a contract test holds the two apart.
    /// </remarks>
    internal static TimeSpan RequestTimeout { get; } = TimeSpan.FromSeconds(3);

    /// <summary>The transport the command uses: no proxy, no redirects.</summary>
    /// <remarks>
    /// An ambient <c>HTTP_PROXY</c> in a container's environment must not route the gateway's own
    /// health check somewhere else, and a redirect would make the answer come from another endpoint.
    /// </remarks>
    internal static SocketsHttpHandler CreateHandler() => new()
    {
        UseProxy = false,
        AllowAutoRedirect = false,
    };

    /// <summary>
    /// Probes the target named by the single argument and writes one line describing the result.
    /// </summary>
    /// <returns><see cref="Healthy"/> on any 2xx answer; otherwise <see cref="Unhealthy"/>.</returns>
    internal static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TimeProvider timeProvider,
        HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(handler);

        if (args.Count != 1 || !TryParseTarget(args[0], out var target))
        {
            await output.WriteLineAsync(RefusedTarget).ConfigureAwait(false);
            return Unhealthy;
        }

        var (exitCode, line) = await ProbeAsync(target, timeProvider, handler).ConfigureAwait(false);

        await output.WriteLineAsync(line).ConfigureAwait(false);
        return exitCode;
    }

    /// <summary>
    /// Accepts an absolute <c>http</c> URL without user information whose host is a loopback IP
    /// literal (127.0.0.0/8 or <c>::1</c>).
    /// </summary>
    internal static bool TryParseTarget(string? value, [NotNullWhen(true)] out Uri? target)
    {
        target = null;

        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || !string.Equals(candidate.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal)
            || candidate.UserInfo.Length != 0
            || candidate.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || !IPAddress.TryParse(candidate.DnsSafeHost, out var address)
            || !IPAddress.IsLoopback(address))
        {
            return false;
        }

        target = candidate;
        return true;
    }

    private static async Task<(int ExitCode, string Line)> ProbeAsync(
        Uri target,
        TimeProvider timeProvider,
        HttpMessageHandler handler)
    {
        using var timeout = new CancellationTokenSource(RequestTimeout, timeProvider);

        // The client's own timeout is off: the probe's budget is the one above, measured by the
        // injected clock, so that a test can expire it without waiting for it.
        using var client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        using var request = new HttpRequestMessage(HttpMethod.Get, target);

        var started = timeProvider.GetTimestamp();

        try
        {
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token)
                .ConfigureAwait(false);

            var status = (int)response.StatusCode;
            var elapsed = (long)timeProvider.GetElapsedTime(started).TotalMilliseconds;

            return status is >= 200 and <= 299
                ? (Healthy, FormattableString.Invariant($"ok: GET {target} answered {status} in {elapsed} ms"))
                : (Unhealthy, FormattableString.Invariant($"unhealthy: GET {target} answered {status} in {elapsed} ms"));
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            var budget = (long)RequestTimeout.TotalMilliseconds;

            return (Unhealthy, FormattableString.Invariant($"unhealthy: GET {target} had no answer within {budget} ms"));
        }
        catch (HttpRequestException exception)
        {
            return (Unhealthy, FormattableString.Invariant($"unhealthy: GET {target} failed: {Describe(exception)}"));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Whatever else goes wrong is still an answer of "unhealthy", never a crash: an unhandled
            // exception would exit with a code Docker does not define.
            return (Unhealthy, FormattableString.Invariant($"unhealthy: GET {target} failed: {exception.GetType().Name}"));
        }
    }

    /// <summary>
    /// The failure class, in words that do not depend on the operating system's language.
    /// </summary>
    private static string Describe(HttpRequestException exception)
    {
        for (Exception? inner = exception; inner is not null; inner = inner.InnerException)
        {
            if (inner is SocketException socket)
            {
                return socket.SocketErrorCode.ToString();
            }
        }

        return exception.HttpRequestError.ToString();
    }
}
