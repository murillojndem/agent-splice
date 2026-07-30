using System.Diagnostics;

namespace AgentSplice.Observability;

/// <summary>
/// Subscribes to AgentSplice's own activity sources so that spans actually exist.
/// </summary>
/// <remarks>
/// Without this, nothing works. <see cref="ActivitySource.StartActivity(string, ActivityKind)"/>
/// returns <c>null</c> when no listener has opted in to that source, and Stage 1A references no
/// OpenTelemetry SDK, so nothing else subscribes to <c>agentsplice.*</c>. Every span would be
/// absent and <c>x-agentsplice-trace-id</c> would never be populated — a header the API contract
/// promises.
///
/// The W3C identifier format is forced because <see cref="Domain.Identifiers.TraceId"/> accepts only
/// a 32-character lowercase hexadecimal value. Under the legacy hierarchical format the identifier
/// would not parse, and the header would silently disappear on exactly the deployments that had not
/// configured tracing.
///
/// Stage 1B replaces this with the OpenTelemetry SDK and must not run both: two listeners on the
/// same source would sample every activity twice.
/// </remarks>
public sealed class AgentSpliceActivityListener : IDisposable
{
    private readonly ActivityListener listener;

    /// <summary>Registers the listener with the process-wide activity infrastructure.</summary>
    public AgentSpliceActivityListener()
    {
        Activity.DefaultIdFormat = ActivityIdFormat.W3C;
        Activity.ForceDefaultIdFormat = true;

        listener = new ActivityListener
        {
            ShouldListenTo = static source => TelemetryNames.Stage1AActivitySources.Contains(source.Name),

            // Every exchange is sampled. Sampling is a volume control for a hosted service; a
            // local-first gateway whose product is evidence must not silently discard some of it.
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            SampleUsingParentId = static (ref ActivityCreationOptions<string> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
        };

        ActivitySource.AddActivityListener(listener);
    }

    /// <inheritdoc />
    public void Dispose() => listener.Dispose();
}
