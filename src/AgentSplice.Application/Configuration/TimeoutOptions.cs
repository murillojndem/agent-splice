namespace AgentSplice.Application.Configuration;

/// <summary>
/// Separate timeout phases for one runtime endpoint
/// (docs/SPECIFICATION.md FR-CHAT-007, FR-CHAT-008).
/// </summary>
/// <remarks>
/// The phases are separate because a local runtime fails in distinguishable ways: refusing the
/// connection, accepting it and never answering, and streaming then stalling mid-generation. A
/// single total timeout reports all three as the same failure and makes the real cause
/// unattributable.
/// </remarks>
public sealed class TimeoutOptions
{
    /// <summary>Maximum time to establish the upstream TCP/TLS connection.</summary>
    public TimeSpan Connect { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Maximum time from sending the request to receiving upstream response headers.</summary>
    public TimeSpan ResponseHeaders { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Maximum gap between two upstream stream events. Generous by default: local generation on a
    /// loaded machine legitimately stalls, and a short idle timeout would abort healthy streams.
    /// </summary>
    public TimeSpan IdleStream { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum total duration of one upstream request.</summary>
    public TimeSpan Total { get; set; } = TimeSpan.FromMinutes(10);
}
