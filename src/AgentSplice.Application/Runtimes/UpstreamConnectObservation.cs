namespace AgentSplice.Application.Runtimes;

/// <summary>
/// When a request had to establish a new upstream connection, and when that finished.
/// </summary>
/// <remarks>
/// Reported by the provider because connection establishment happens inside its transport stack and
/// is invisible to the request path otherwise. Without it, a runtime slow to accept connections and
/// a runtime slow to think are the same number, and they send an operator to different places
/// (docs/OBSERVABILITY.md "Latency phases").
///
/// Two instants rather than a duration, so the timeline carries boundaries and the measurement is
/// derived from them — the same rule every other latency phase follows.
/// </remarks>
public sealed record UpstreamConnectObservation
{
    private UpstreamConnectObservation()
    {
    }

    /// <summary>When the connection attempt began.</summary>
    public DateTimeOffset StartedAt { get; private init; }

    /// <summary>When the connection was established.</summary>
    public DateTimeOffset EstablishedAt { get; private init; }

    /// <summary>Records an established connection.</summary>
    public static UpstreamConnectObservation Create(DateTimeOffset startedAt, DateTimeOffset establishedAt) =>
        new() { StartedAt = startedAt, EstablishedAt = establishedAt };
}
