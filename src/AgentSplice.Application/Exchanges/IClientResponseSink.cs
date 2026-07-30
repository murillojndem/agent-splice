using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Application.Exchanges;

/// <summary>
/// The client half of a response, as the application needs to see it.
/// </summary>
/// <remarks>
/// A port rather than an <c>HttpContext</c> for two reasons. <c>AgentSplice.Application</c> may not
/// reference <c>Microsoft.AspNetCore</c>, and the relay is orchestration, which CLAUDE.md requires to
/// be testable without a server — a pump that can only be exercised through a live socket is a pump
/// whose failure paths are never exercised at all.
///
/// No transport exception crosses this boundary. A vanished client is a returned value, symmetric
/// with <see cref="Runtimes.IUpstreamResponseBody"/>, because a disconnect is an expected way for a
/// completion to end rather than an error.
/// </remarks>
public interface IClientResponseSink
{
    /// <summary>True once status and headers are committed and can no longer be changed.</summary>
    bool HasStarted { get; }

    /// <summary>Commits status, headers, and media type, and disables transport buffering.</summary>
    ValueTask<ClientWriteResult> StartAsync(ClientResponseStart start, CancellationToken cancellationToken);

    /// <summary>Writes bytes and flushes them before returning.</summary>
    /// <remarks>
    /// Flushing is part of the contract rather than a separate call, because a write that might not
    /// be flushed makes every claim about event timing conditional on a caller remembering to ask.
    /// </remarks>
    ValueTask<ClientWriteResult> WriteAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);

    /// <summary>Abandons a response that has already started.</summary>
    /// <remarks>
    /// The only way left to say "this is not a whole answer" once the status line is committed. An
    /// event stream that stops early but closes cleanly at the HTTP level is indistinguishable from a
    /// complete one for any client that does not require a protocol terminator, so ending politely
    /// would turn a failure into an apparent success.
    /// </remarks>
    void Abort();
}
