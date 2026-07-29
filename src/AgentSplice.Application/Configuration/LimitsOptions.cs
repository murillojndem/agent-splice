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
}
