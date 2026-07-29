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
}
