using System.Net.Sockets;
using AgentSplice.Application.Runtimes;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Turns a transport exception into a classified, transport-free failure.
/// </summary>
/// <remarks>
/// This is the boundary that lets <c>AgentSplice.Application</c> avoid referencing
/// <c>System.Net.Http</c> at all, which an architecture test enforces. It is also the only place
/// that can tell a client disconnect from a timeout, because that distinction lives in <em>which</em>
/// cancellation token fired, not in the exception type: both surface as
/// <see cref="OperationCanceledException"/>.
/// </remarks>
internal static class UpstreamFailureClassifier
{
    /// <summary>
    /// Classifies a cancellation by asking which budget actually elapsed.
    /// </summary>
    /// <remarks>
    /// Order matters and is now four deep. The client token is checked first, because a disconnect is
    /// not a runtime defect and must never be reported as one. The total budget comes next because
    /// every other source is linked to it and is therefore signalled together with it; checking any
    /// of them first would misreport every total-budget expiry as something narrower.
    ///
    /// The idle budget is checked before the response-header budget because a stream that outlives
    /// its header budget is entirely normal — headers arrived long ago — and checking headers first
    /// would report every mid-stream stall as a runtime that was slow to answer.
    /// </remarks>
    internal static UpstreamFailure ClassifyCancellation(
        CancellationToken clientToken,
        CancellationToken totalToken,
        CancellationToken responseHeadersToken,
        CancellationToken idleStreamToken = default)
    {
        if (clientToken.IsCancellationRequested)
        {
            return UpstreamFailure.Create(UpstreamFailureReason.Cancelled);
        }

        if (totalToken.IsCancellationRequested)
        {
            return UpstreamFailure.Timeout(TimeoutPhase.Total);
        }

        if (idleStreamToken.IsCancellationRequested)
        {
            return UpstreamFailure.Timeout(TimeoutPhase.IdleStream);
        }

        return responseHeadersToken.IsCancellationRequested
            ? UpstreamFailure.Timeout(TimeoutPhase.ResponseHeaders)

            // Nothing we own fired. The most likely cause is the handler's own connect budget, which
            // surfaces as a cancellation without setting any of our tokens.
            : UpstreamFailure.Timeout(TimeoutPhase.Connect);
    }

    /// <summary>Classifies a transport-level request failure.</summary>
    internal static UpstreamFailure ClassifyRequestFailure(HttpRequestException exception)
    {
        // A connect timeout is surfaced by SocketsHttpHandler as a request failure wrapping a
        // TimeoutException, not as a cancellation, so it has to be recognised here as well.
        if (Contains<TimeoutException>(exception))
        {
            return UpstreamFailure.Timeout(TimeoutPhase.Connect);
        }

        return UpstreamFailure.Create(
            UpstreamFailureReason.Unreachable,
            details: Describe(exception));
    }

    /// <summary>
    /// Describes a transport failure without leaking where AgentSplice was trying to connect.
    /// </summary>
    /// <remarks>
    /// The exception message routinely contains the host and port, and an internal address in a
    /// client-visible error is exactly what docs/SECURITY.md "Trace safety" forbids. Only the socket
    /// error name is kept, which is a bounded token from a fixed enumeration.
    /// </remarks>
    private static Domain.Observations.SafeDetails Describe(HttpRequestException exception)
    {
        var socketError = Find<SocketException>(exception)?.SocketErrorCode;

        return socketError is null
            ? Domain.Observations.SafeDetails.Empty
            : Domain.Observations.SafeDetails.Create("upstream.socket_error", socketError.Value.ToString());
    }

    private static bool Contains<TException>(Exception exception)
        where TException : Exception =>
        Find<TException>(exception) is not null;

    private static TException? Find<TException>(Exception? exception)
        where TException : Exception
    {
        while (exception is not null)
        {
            if (exception is TException match)
            {
                return match;
            }

            exception = exception.InnerException;
        }

        return null;
    }
}
