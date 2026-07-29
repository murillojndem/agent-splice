using System.Collections.ObjectModel;
using System.Net;

namespace AgentSplice.TestSupport.FakeUpstream;

/// <summary>
/// A fully scripted upstream response, including its timing and its failure mode.
/// </summary>
/// <remarks>
/// Everything a Stage 1 test needs to provoke is expressible here: delayed headers, delayed events,
/// byte-level chunking, a trailing stall, and an abrupt connection reset. Real LM Studio can produce
/// all of these, but not on demand, which is why the deterministic fake is the primary fixture and
/// real-runtime tests are optional (docs/TESTING.md).
/// </remarks>
public sealed record UpstreamResponseScript
{
    /// <summary>HTTP status code to return.</summary>
    public int StatusCode { get; init; } = (int)HttpStatusCode.OK;

    /// <summary>Response content type, or <c>null</c> to send none.</summary>
    public string? ContentType { get; init; } = "application/json";

    /// <summary>Additional response headers.</summary>
    public IReadOnlyDictionary<string, string> Headers { get; init; } =
        ReadOnlyDictionary<string, string>.Empty;

    /// <summary>Delay before response headers are sent, for response-header timeout tests.</summary>
    public TimeSpan HeaderDelay { get; init; }

    /// <summary>
    /// Body written as a single chunk. Mutually exclusive with <see cref="Chunks"/>.
    /// </summary>
    public ReadOnlyMemory<byte>? Body { get; init; }

    /// <summary>
    /// Body written as a sequence of timed chunks. Mutually exclusive with <see cref="Body"/>.
    /// </summary>
    public IReadOnlyList<UpstreamChunk> Chunks { get; init; } = ReadOnlyCollection<UpstreamChunk>.Empty;

    /// <summary>
    /// Delay after the last byte and before the response completes, for idle-stream timeout tests.
    /// </summary>
    public TimeSpan TrailingDelay { get; init; }

    /// <summary>
    /// Resets the connection instead of completing the response, producing a premature EOF for the
    /// client (docs/TESTING.md SSE fixture family).
    /// </summary>
    public bool ClosePrematurely { get; init; }
}
