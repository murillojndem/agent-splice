using System.Net.Sockets;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// When a request had to open a new connection, and how long that took.
/// </summary>
/// <remarks>
/// Connection establishment happens inside the handler, so it is invisible to the request path
/// unless the handler is asked to report it. Without this, a runtime that is slow to accept
/// connections is indistinguishable from one that is slow to think — and those point an operator at
/// completely different problems (docs/OBSERVABILITY.md "Latency phases").
///
/// Absent when the request reused a pooled connection, which is the common case and is a genuine
/// "no connection was established" rather than a zero.
/// </remarks>
internal sealed record UpstreamConnectTiming(DateTimeOffset StartedAt, DateTimeOffset CompletedAt)
{
    /// <summary>
    /// The key the connect callback stashes timing under, read back after the response arrives.
    /// </summary>
    /// <remarks>
    /// Carried on the request rather than in ambient state, because the callback fires for the
    /// request that triggered the connection and only that request should be charged for it.
    /// </remarks>
    internal static HttpRequestOptionsKey<UpstreamConnectTiming> OptionsKey { get; } =
        new("agentsplice.upstream.connect");

    /// <summary>Opens a connection and records what it cost.</summary>
    /// <remarks>
    /// Reproduces the handler's own default — a TCP socket with Nagle disabled — because the only
    /// reason to take over connection establishment here is to time it. Anything else this changed
    /// would be an unintended side effect of an observability decision.
    /// </remarks>
    internal static async ValueTask<Stream> ConnectAsync(
        SocketsHttpConnectionContext context,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        var startedAt = timeProvider.GetUtcNow();

        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);

            context.InitialRequestMessage.Options.Set(
                OptionsKey,
                new UpstreamConnectTiming(startedAt, timeProvider.GetUtcNow()));

            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            // A failed connect leaves no timing behind. It is not a connection that took a while; it
            // is a connection that never existed, and the failure itself is what gets classified.
            socket.Dispose();
            throw;
        }
    }
}
