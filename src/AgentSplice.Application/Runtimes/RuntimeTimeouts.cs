namespace AgentSplice.Application.Runtimes;

/// <summary>
/// Immutable projection of <see cref="Configuration.TimeoutOptions"/> for one runtime
/// (docs/SPECIFICATION.md FR-CHAT-007, FR-CHAT-008).
/// </summary>
public sealed record RuntimeTimeouts
{
    private RuntimeTimeouts()
    {
    }

    /// <summary>Maximum time to establish the upstream connection.</summary>
    public TimeSpan Connect { get; private init; }

    /// <summary>Maximum time from sending the request to receiving upstream response headers.</summary>
    public TimeSpan ResponseHeaders { get; private init; }

    /// <summary>
    /// Maximum time, on a stream, from the response headers to the first byte of the body.
    /// </summary>
    /// <remarks>
    /// The wait before a stream's first byte is the runtime queueing the request and processing its
    /// prompt, which on local hardware legitimately takes minutes, so it has a budget of its own rather
    /// than the idle one (ADR 0028). A response that is not streamed has no such budget: its body is
    /// bounded by <see cref="Total"/>.
    /// </remarks>
    public TimeSpan FirstByte { get; private init; }

    /// <summary>
    /// Maximum gap between two upstream reads after a stream's first byte.
    /// </summary>
    /// <remarks>
    /// Measures the gap between bytes rather than the life of the stream, so a long generation is not
    /// mistaken for a stalled one. The whole stream is bounded separately by <see cref="Total"/>, and
    /// the wait for its first byte by <see cref="FirstByte"/>.
    /// </remarks>
    public TimeSpan IdleStream { get; private init; }

    /// <summary>Maximum total duration of one upstream request.</summary>
    public TimeSpan Total { get; private init; }

    /// <summary>Creates a validated timeout set.</summary>
    /// <remarks>
    /// Five arguments of one type, so callers name them: a positional swap would compile and move a
    /// budget onto the wrong phase.
    /// </remarks>
    public static RuntimeTimeouts Create(
        TimeSpan connect,
        TimeSpan responseHeaders,
        TimeSpan firstByte,
        TimeSpan idleStream,
        TimeSpan total)
    {
        var phases = new (string Name, TimeSpan Value)[]
        {
            (nameof(connect), connect),
            (nameof(responseHeaders), responseHeaders),
            (nameof(firstByte), firstByte),
            (nameof(idleStream), idleStream),
            (nameof(total), total),
        };

        foreach (var (name, value) in phases)
        {
            if (value <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(name, value, "A timeout phase must be positive.");
            }
        }

        return new RuntimeTimeouts
        {
            Connect = connect,
            ResponseHeaders = responseHeaders,
            FirstByte = firstByte,
            IdleStream = idleStream,
            Total = total,
        };
    }
}
