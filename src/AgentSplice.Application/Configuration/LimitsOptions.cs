namespace AgentSplice.Application.Configuration;

/// <summary>
/// Size bounds on what AgentSplice will read in either direction
/// (docs/SECURITY.md "Apply request-body, header, event, stream-duration, and concurrency limits").
/// </summary>
/// <remarks>
/// The upstream bounds exist because the non-streaming path is deliberately fully buffered: the
/// whole completion is held in memory so it can be forwarded verbatim. Without a ceiling, a
/// defective or hostile runtime could answer a small request with hundreds of megabytes and turn a
/// single completion into memory pressure for the whole gateway.
///
/// Reading stops at the limit plus one byte, so exceeding a bound is detected without ever
/// materialising the oversized payload.
/// </remarks>
public sealed class LimitsOptions
{
    /// <summary>Largest client request body that will be read. Default 4 MiB.</summary>
    public long MaxRequestBodyBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>Largest upstream completion body that will be read. Default 64 MiB.</summary>
    public long MaxUpstreamCompletionBodyBytes { get; set; } = 64L * 1024 * 1024;

    /// <summary>
    /// Largest upstream model-catalogue body that will be read. Default 4 MiB. Separate from the
    /// completion bound because a catalogue is a small, predictable document: a runtime answering
    /// model discovery with a completion-sized payload is a defect worth failing on early.
    /// </summary>
    public long MaxCatalogueBodyBytes { get; set; } = 4L * 1024 * 1024;

    /// <summary>
    /// Largest single streamed event AgentSplice will hold while assembling it. Default 1 MiB.
    /// </summary>
    /// <remarks>
    /// The one place the streaming path's memory is not already bounded. Bytes reach the client and
    /// are released immediately, so retention per active stream is a read buffer plus the event
    /// currently being assembled — which makes this bound, times the concurrency limit, the whole
    /// memory ceiling of the streaming path.
    ///
    /// Exceeding it stops the relay. A bound that kept going after being crossed would not be one.
    /// </remarks>
    public int MaxStreamEventBytes { get; set; } = 1024 * 1024;

    /// <summary>Completion requests served concurrently. Default 64.</summary>
    /// <remarks>
    /// The buffered path holds a whole request and a whole response in memory and the streaming path
    /// holds a buffer per stream, so without this the gateway's peak memory is bounded only by how
    /// many callers happen to arrive at once. Refusing is preferred to queueing: an agent loop can
    /// act on a refusal and can only wait out a queue.
    /// </remarks>
    public int MaxConcurrentCompletions { get; set; } = 64;
}
