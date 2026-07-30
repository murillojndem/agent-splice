namespace AgentSplice.Application.Runtimes;

/// <summary>
/// An open upstream response body, read incrementally and classified rather than thrown.
/// </summary>
/// <remarks>
/// A raw <see cref="System.IO.Stream"/> would satisfy the module boundaries just as well, but it
/// would move the wrong thing across them. The difference between a client disconnect, an
/// idle-stream stall, and an expired total budget is <em>which</em> cancellation token fired, and
/// those tokens exist only inside the provider. Returning a classified result keeps that attribution
/// where the evidence is, and keeps the application free of both <c>System.Net.Http</c> and the
/// transport exceptions it throws.
///
/// The caller owns the buffer, so relaying a stream of any length allocates nothing per read.
///
/// Disposing releases the response, the connection, and the timeout budgets. The lifetime is the
/// stream's rather than the call's, which is exactly why this exists as a returned resource instead
/// of a scope inside the provider.
/// </remarks>
public interface IUpstreamResponseBody : IAsyncDisposable
{
    /// <summary>Reads whatever has arrived, or reports why nothing more will.</summary>
    /// <param name="buffer">Caller-owned destination.</param>
    /// <param name="cancellationToken">
    /// The client's token. The provider links its own budgets to it, so cancelling here stops the
    /// runtime as well rather than merely stopping AgentSplice from listening.
    /// </param>
    ValueTask<UpstreamReadResult> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken);
}
