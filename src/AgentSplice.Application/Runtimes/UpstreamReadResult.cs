namespace AgentSplice.Application.Runtimes;

/// <summary>
/// The outcome of one read from an upstream stream: bytes, a clean end, or a classified failure.
/// </summary>
/// <remarks>
/// Three outcomes rather than two, because "the runtime finished" and "the connection died" are the
/// same zero-length read at the socket and completely different facts in a trace. Collapsing them is
/// what makes a truncated response indistinguishable from a complete one.
/// </remarks>
public readonly record struct UpstreamReadResult
{
    private UpstreamReadResult(int bytesRead, bool endOfStream, UpstreamFailure? failure)
    {
        BytesRead = bytesRead;
        EndOfStream = endOfStream;
        Failure = failure;
    }

    /// <summary>Bytes written into the caller's buffer.</summary>
    public int BytesRead { get; }

    /// <summary>True when the runtime closed the body cleanly.</summary>
    public bool EndOfStream { get; }

    /// <summary>Why no more bytes will arrive, or <c>null</c> when the read succeeded.</summary>
    public UpstreamFailure? Failure { get; }

    /// <summary>True when the stream ended for a reason that is not a clean close.</summary>
    public bool Faulted => Failure is not null;

    /// <summary>Records bytes read.</summary>
    public static UpstreamReadResult Bytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        return new UpstreamReadResult(count, endOfStream: false, failure: null);
    }

    /// <summary>Records that the runtime closed the body cleanly.</summary>
    public static UpstreamReadResult Completed { get; } = new(0, endOfStream: true, failure: null);

    /// <summary>Records why the stream ended before the runtime finished.</summary>
    public static UpstreamReadResult Failed(UpstreamFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);

        return new UpstreamReadResult(0, endOfStream: false, failure);
    }
}
