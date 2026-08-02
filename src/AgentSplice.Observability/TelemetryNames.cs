using System.Collections.Frozen;

namespace AgentSplice.Observability;

/// <summary>
/// Normative OpenTelemetry names (docs/SPECIFICATION.md section 15.4).
/// </summary>
/// <remarks>
/// Declared as constants rather than assembled at each call site so that a rename is a single
/// reviewable change: these names are what external collectors and dashboards subscribe to, and they
/// are verified against the specification by a contract test.
///
/// Only the Stage 1 activity sources appear here. Replay, conformance, evaluation, and adapter
/// sources are added by the stage that can emit them.
/// </remarks>
public static class TelemetryNames
{
    /// <summary>The single AgentSplice meter.</summary>
    public const string Meter = "AgentSplice";

    /// <summary>Activity source names for the Stage 1 request path.</summary>
    public static class ActivitySources
    {
        /// <summary>One span per completion exchange.</summary>
        public const string Exchange = "agentsplice.exchange";

        /// <summary>One span per upstream provider request.</summary>
        public const string ProviderRequest = "agentsplice.provider.request";

        /// <summary>One span covering stream forwarding.</summary>
        public const string Stream = "agentsplice.stream";

        /// <summary>One span per metadata persistence operation.</summary>
        public const string Persistence = "agentsplice.persistence";
    }

    /// <summary>Every activity source name enabled in the current stage.</summary>
    public static FrozenSet<string> Stage1ActivitySources { get; } = new[]
    {
        ActivitySources.Exchange,
        ActivitySources.ProviderRequest,
        ActivitySources.Stream,
        ActivitySources.Persistence,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// The activity sources this stage actually starts spans on.
    /// </summary>
    /// <remarks>
    /// A subset of <see cref="Stage1ActivitySources"/>, which mirrors the specification's Stage 1
    /// list. Subscribing to a source nothing writes to would let a dashboard show a permanently
    /// empty panel and read as a capability that exists — the same reason later-stage sources are
    /// not declared at all.
    /// </remarks>
    public static FrozenSet<string> LiveActivitySources { get; } = new[]
    {
        ActivitySources.Exchange,
        ActivitySources.ProviderRequest,
        ActivitySources.Stream,
        ActivitySources.Persistence,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>Instrument names from docs/OBSERVABILITY.md.</summary>
    public static class Instruments
    {
        /// <summary>Completion exchanges started.</summary>
        public const string Exchanges = "agentsplice.exchanges";

        /// <summary>Exchanges currently in flight.</summary>
        public const string ActiveExchanges = "agentsplice.active_exchanges";

        /// <summary>End-to-end duration of an exchange.</summary>
        public const string ExchangeDuration = "agentsplice.exchange.duration";

        /// <summary>Duration of the upstream call.</summary>
        public const string UpstreamDuration = "agentsplice.upstream.duration";

        /// <summary>Time from opening the upstream request to its response headers.</summary>
        public const string TimeToHeaders = "agentsplice.time_to_headers";

        /// <summary>Prompt tokens, as reported.</summary>
        public const string PromptTokens = "agentsplice.prompt.tokens";

        /// <summary>Completion tokens, as reported.</summary>
        public const string CompletionTokens = "agentsplice.completion.tokens";

        /// <summary>Duration of a model discovery refresh.</summary>
        public const string ModelDiscoveryDuration = "agentsplice.model_discovery.duration";

        /// <summary>Time from opening the upstream request to its first body byte.</summary>
        public const string TimeToFirstByte = "agentsplice.time_to_first_byte";

        /// <summary>Time from accepting the request to the first event carrying model output.</summary>
        public const string TimeToFirstSemanticEvent = "agentsplice.time_to_first_semantic_event";

        /// <summary>Time from accepting the request to the first whole event flushed to the client.</summary>
        public const string TimeToFirstClientEvent = "agentsplice.time_to_first_client_event";

        /// <summary>Events delivered to the client.</summary>
        public const string StreamEvents = "agentsplice.stream.events";

        /// <summary>Bytes forwarded to the client.</summary>
        public const string StreamBytes = "agentsplice.stream.bytes";

        /// <summary>Generation throughput over the observed decode window.</summary>
        public const string GenerationThroughput = "agentsplice.generation.tokens_per_second";

        /// <summary>Exchanges whose evidence did not reach the metadata store.</summary>
        public const string PersistenceFailures = "agentsplice.persistence.failures";
    }

    /// <summary>
    /// The instruments this stage can honestly emit.
    /// </summary>
    /// <remarks>
    /// The streaming instruments record only what a streamed exchange observed; a buffered one emits
    /// no data point rather than a zero, because in a series where the value means something a zero
    /// reads as "this happened, and it was none" (FR-OBS-004).
    ///
    /// <c>agentsplice.prompt.tokens_per_second</c> is deliberately absent and not merely deferred.
    /// Nothing AgentSplice can observe marks the end of prompt processing, so the only interval
    /// available is time to first token — which measures the prompt, the queue, and the network
    /// together. Publishing that under a prompt-throughput name is precisely the conflation
    /// FR-OBS-005 exists to prevent, and no later stage changes that without runtime-log evidence.
    /// </remarks>
    public static FrozenSet<string> LiveInstruments { get; } = new[]
    {
        Instruments.Exchanges,
        Instruments.ActiveExchanges,
        Instruments.ExchangeDuration,
        Instruments.UpstreamDuration,
        Instruments.TimeToHeaders,
        Instruments.PromptTokens,
        Instruments.CompletionTokens,
        Instruments.ModelDiscoveryDuration,
        Instruments.TimeToFirstByte,
        Instruments.TimeToFirstSemanticEvent,
        Instruments.TimeToFirstClientEvent,
        Instruments.StreamEvents,
        Instruments.StreamBytes,
        Instruments.GenerationThroughput,
        Instruments.PersistenceFailures,
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Bounded attribute and dimension names (docs/OBSERVABILITY.md, FR-OBS-006).
    /// </summary>
    /// <remarks>
    /// Every name here has a small, closed value set. A model identifier is deliberately absent:
    /// it is client-supplied and unbounded, so using it as a dimension would let one caller multiply
    /// the cardinality of every series without limit.
    /// </remarks>
    public static class Attributes
    {
        /// <summary>Which client-facing protocol the request arrived on.</summary>
        public const string IngressProtocol = "agentsplice.ingress.protocol";

        /// <summary>The normalised runtime endpoint identifier.</summary>
        public const string RuntimeId = "agentsplice.runtime.id";

        /// <summary>The provider adapter key.</summary>
        public const string ProviderType = "agentsplice.provider.type";

        /// <summary>Whether a streamed response was requested.</summary>
        public const string Streaming = "agentsplice.streaming";

        /// <summary>The exchange's terminal state.</summary>
        public const string ExchangeStatus = "agentsplice.exchange.status";

        /// <summary>The coarse class of the upstream status, such as <c>2xx</c>.</summary>
        public const string UpstreamStatusClass = "agentsplice.upstream.status_class";

        /// <summary>The stable error type, when the exchange produced one.</summary>
        public const string ErrorType = "error.type";

        /// <summary>
        /// How a stream ended, attached only to exchanges that actually streamed.
        /// </summary>
        /// <remarks>
        /// A closed set of ten values, and absent entirely from buffered exchanges so that adding it
        /// costs the existing series no cardinality at all.
        /// </remarks>
        public const string StreamTermination = "agentsplice.stream.termination";

        /// <summary>
        /// Why evidence was not retained, attached only to the persistence-failure counter.
        /// </summary>
        /// <remarks>
        /// Two values. Saturation and a rejected write are different operational problems with
        /// different fixes, and a single undifferentiated failure count would send an operator adding
        /// queue capacity to a store that is refusing every write.
        /// </remarks>
        public const string PersistenceFailureReason = "agentsplice.persistence.failure_reason";
    }

    /// <summary>Every dimension name this stage may attach.</summary>
    public static FrozenSet<string> LiveAttributes { get; } = new[]
    {
        Attributes.IngressProtocol,
        Attributes.RuntimeId,
        Attributes.ProviderType,
        Attributes.Streaming,
        Attributes.ExchangeStatus,
        Attributes.UpstreamStatusClass,
        Attributes.ErrorType,
        Attributes.StreamTermination,
        Attributes.PersistenceFailureReason,
    }.ToFrozenSet(StringComparer.Ordinal);
}
