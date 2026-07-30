using AgentSplice.Application.Runtimes;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// One open LM Studio response body, with its own timeout budgets and their attribution.
/// </summary>
/// <remarks>
/// Owns the response message, the body stream, and the linked cancellation sources, because their
/// lifetime is the stream's rather than the call's. That ownership is the whole reason this type
/// exists: a <c>using</c> scope inside the provider would close the connection at the moment the
/// relay was about to start reading from it.
///
/// The idle budget is re-armed per read rather than allocated per read. <c>CancelAfter</c> creates
/// its timer once and reschedules it thereafter, so a stream of any length costs one timer and no
/// allocations on the read path.
/// </remarks>
internal sealed class LmStudioUpstreamResponseBody : IUpstreamResponseBody
{
    private readonly HttpResponseMessage response;
    private readonly Stream body;
    private readonly CancellationTokenSource total;
    private readonly CancellationTokenSource responseHeaders;
    private readonly CancellationTokenSource idle;
    private readonly TimeSpan idleBudget;
    private readonly CancellationToken clientToken;

    private bool disposed;

    internal LmStudioUpstreamResponseBody(
        HttpResponseMessage response,
        Stream body,
        CancellationTokenSource total,
        CancellationTokenSource responseHeaders,
        CancellationTokenSource idle,
        TimeSpan idleBudget,
        CancellationToken clientToken)
    {
        this.response = response;
        this.body = body;
        this.total = total;
        this.responseHeaders = responseHeaders;
        this.idle = idle;
        this.idleBudget = idleBudget;
        this.clientToken = clientToken;
    }

    /// <inheritdoc />
    public async ValueTask<UpstreamReadResult> ReadAsync(
        Memory<byte> buffer,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);

        // Re-armed before every read, so the budget measures the gap between bytes rather than the
        // life of the stream. A long generation is not a stalled one, and a single budget covering
        // both would have to be set so loosely that it caught nothing.
        idle.CancelAfter(idleBudget);

        try
        {
            var read = await body.ReadAsync(buffer, idle.Token).ConfigureAwait(false);

            return read > 0 ? UpstreamReadResult.Bytes(read) : UpstreamReadResult.Completed;
        }
        catch (OperationCanceledException)
        {
            return UpstreamReadResult.Failed(UpstreamFailureClassifier.ClassifyCancellation(
                cancellationToken.IsCancellationRequested ? cancellationToken : clientToken,
                total.Token,
                responseHeaders.Token,
                idle.Token));
        }
        catch (Exception exception) when (exception is IOException or HttpRequestException)
        {
            // The runtime went away mid-body. It was reachable and it answered, so this is a lost
            // connection rather than an unreachable endpoint — a distinction that decides whether an
            // operator looks at the network or at the model.
            return UpstreamReadResult.Failed(UpstreamFailure.Create(
                UpstreamFailureReason.InvalidResponse,
                details: Domain.Observations.SafeDetails.Create("upstream.stream", "connection.lost")));
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;

        await body.DisposeAsync().ConfigureAwait(false);

        response.Dispose();
        idle.Dispose();
        responseHeaders.Dispose();
        total.Dispose();
    }
}
