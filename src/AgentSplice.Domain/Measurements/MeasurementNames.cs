namespace AgentSplice.Domain.Measurements;

/// <summary>
/// Stable measurement names for the Stage 1 latency and throughput model
/// (docs/SPECIFICATION.md sections 15.2 and 15.3).
/// </summary>
/// <remarks>
/// Prompt and generation throughput are separate names, and neither is called simply
/// "tokens_per_second". Labelling prompt processing as generation throughput is the specific
/// reporting error CLAUDE.md calls out.
/// </remarks>
public static class MeasurementNames
{
    /// <summary>Time spent validating and parsing the ingress envelope.</summary>
    public const string ValidationDuration = "gateway.validation.duration";

    /// <summary>Time spent resolving the model alias and runtime.</summary>
    public const string RoutingDuration = "gateway.routing.duration";

    /// <summary>Time to establish the upstream connection.</summary>
    public const string UpstreamConnectDuration = "upstream.connect.duration";

    /// <summary>Time from opening the upstream request to receiving response headers.</summary>
    public const string UpstreamHeadersDuration = "upstream.headers.duration";

    /// <summary>Time from opening the upstream request to the first upstream body byte.</summary>
    public const string TimeToFirstUpstreamByte = "upstream.first_byte.duration";

    /// <summary>Time from accepting the request to the first semantic output event.</summary>
    public const string TimeToFirstSemanticEvent = "exchange.first_semantic_event.duration";

    /// <summary>Time from accepting the request to the first event flushed to the client.</summary>
    public const string TimeToFirstClientEvent = "exchange.first_client_event.duration";

    /// <summary>Total wall-clock duration of the exchange.</summary>
    public const string TotalDuration = "exchange.total.duration";

    /// <summary>Time spent persisting exchange metadata.</summary>
    public const string PersistenceDuration = "persistence.duration";

    /// <summary>Prompt tokens consumed by the exchange.</summary>
    public const string PromptTokens = "usage.prompt.tokens";

    /// <summary>Completion tokens produced by the exchange.</summary>
    public const string CompletionTokens = "usage.completion.tokens";

    /// <summary>Prompt processing throughput. Never interchangeable with generation throughput.</summary>
    public const string PromptThroughput = "usage.prompt.tokens_per_second";

    /// <summary>Generation throughput. Never interchangeable with prompt throughput.</summary>
    public const string GenerationThroughput = "usage.generation.tokens_per_second";

    /// <summary>Bytes forwarded to the client.</summary>
    public const string ClientResponseBytes = "stream.client.bytes";

    /// <summary>SSE events forwarded to the client.</summary>
    public const string ClientStreamEvents = "stream.client.events";
}
