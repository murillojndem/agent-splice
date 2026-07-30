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
    /// Maximum gap between two upstream reads while a stream is open.
    /// </summary>
    /// <remarks>
    /// Measures the gap between bytes rather than the life of the stream, so a long generation is not
    /// mistaken for a stalled one. The whole stream is bounded separately by <see cref="Total"/>.
    /// </remarks>
    public TimeSpan IdleStream { get; private init; }

    /// <summary>Maximum total duration of one upstream request.</summary>
    public TimeSpan Total { get; private init; }

    /// <summary>Creates a validated timeout set.</summary>
    public static RuntimeTimeouts Create(
        TimeSpan connect,
        TimeSpan responseHeaders,
        TimeSpan idleStream,
        TimeSpan total)
    {
        var phases = new (string Name, TimeSpan Value)[]
        {
            (nameof(connect), connect),
            (nameof(responseHeaders), responseHeaders),
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
            IdleStream = idleStream,
            Total = total,
        };
    }
}
