namespace AgentSplice.Application.Configuration;

/// <summary>
/// Separate timeout phases for one runtime endpoint
/// (docs/SPECIFICATION.md FR-CHAT-007, FR-CHAT-008).
/// </summary>
/// <remarks>
/// The phases are separate because a local runtime fails in distinguishable ways: refusing the
/// connection, accepting it and never answering, starting a stream and never sending its first byte,
/// and streaming then stalling mid-generation. A single total timeout reports all of them as the same
/// failure and makes the real cause unattributable.
/// </remarks>
public sealed class TimeoutOptions
{
    /// <summary>Maximum time to establish the upstream TCP/TLS connection.</summary>
    public TimeSpan Connect { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Maximum time from sending the request to receiving upstream response headers. Generous by
    /// default: a runtime may send no headers until it has loaded the model and processed the prompt,
    /// or, for a request that is not streamed, until it has generated the whole answer (ADR 0028).
    /// </summary>
    public TimeSpan ResponseHeaders { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum time, on a stream, from receiving the response headers to receiving the first byte of
    /// the body. Generous by default: a runtime that sends a stream's headers at once sends nothing
    /// more while the request waits its turn and its prompt is processed (ADR 0028).
    /// </summary>
    public TimeSpan FirstByte { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Maximum gap between two upstream reads after a stream's first byte. Generous by default: local
    /// generation on a loaded machine legitimately stalls, and a short idle timeout would abort healthy
    /// streams.
    /// </summary>
    public TimeSpan IdleStream { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Maximum total duration of one upstream request.</summary>
    public TimeSpan Total { get; set; } = TimeSpan.FromMinutes(10);
}
