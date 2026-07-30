using System.Diagnostics;
using System.Diagnostics.Metrics;
using AgentSplice.Application.Observability;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Observability;

/// <summary>
/// Emits AgentSplice's spans and metrics through <c>System.Diagnostics</c>.
/// </summary>
/// <remarks>
/// No OpenTelemetry SDK is referenced in this stage, which an architecture test enforces. The
/// primitives here are the ones the SDK itself consumes, so adopting it later is a matter of adding
/// an exporter rather than rewriting instrumentation.
/// </remarks>
public sealed class ExchangeTelemetry : IExchangeTelemetry, IDisposable
{
    private readonly ActivitySource exchangeSource = new(TelemetryNames.ActivitySources.Exchange);
    private readonly ActivitySource providerSource = new(TelemetryNames.ActivitySources.ProviderRequest);
    private readonly Meter meter = new(TelemetryNames.Meter);

    private readonly Counter<long> exchanges;
    private readonly UpDownCounter<long> activeExchanges;
    private readonly Histogram<double> exchangeDuration;
    private readonly Histogram<double> upstreamDuration;
    private readonly Histogram<double> timeToHeaders;
    private readonly Histogram<long> promptTokens;
    private readonly Histogram<long> completionTokens;
    private readonly Histogram<double> discoveryDuration;

    /// <summary>Creates the instruments.</summary>
    public ExchangeTelemetry()
    {
        exchanges = meter.CreateCounter<long>(
            TelemetryNames.Instruments.Exchanges,
            unit: "{exchange}",
            description: "Completion exchanges started.");

        activeExchanges = meter.CreateUpDownCounter<long>(
            TelemetryNames.Instruments.ActiveExchanges,
            unit: "{exchange}",
            description: "Completion exchanges currently in flight.");

        exchangeDuration = meter.CreateHistogram<double>(
            TelemetryNames.Instruments.ExchangeDuration,
            unit: "ms",
            description: "End-to-end duration of a completion exchange.");

        upstreamDuration = meter.CreateHistogram<double>(
            TelemetryNames.Instruments.UpstreamDuration,
            unit: "ms",
            description: "Duration of the upstream provider call.");

        timeToHeaders = meter.CreateHistogram<double>(
            TelemetryNames.Instruments.TimeToHeaders,
            unit: "ms",
            description: "Time from opening the upstream request to its response headers.");

        promptTokens = meter.CreateHistogram<long>(
            TelemetryNames.Instruments.PromptTokens,
            unit: "{token}",
            description: "Prompt tokens as reported by the runtime.");

        completionTokens = meter.CreateHistogram<long>(
            TelemetryNames.Instruments.CompletionTokens,
            unit: "{token}",
            description: "Completion tokens as reported by the runtime.");

        discoveryDuration = meter.CreateHistogram<double>(
            TelemetryNames.Instruments.ModelDiscoveryDuration,
            unit: "ms",
            description: "Duration of a model discovery refresh.");
    }

    /// <inheritdoc />
    public IExchangeTrace StartExchange()
    {
        activeExchanges.Add(1);

        return new ExchangeTrace(
            exchangeSource.StartActivity(TelemetryNames.ActivitySources.Exchange, ActivityKind.Server),
            activeExchanges);
    }

    /// <inheritdoc />
    public IDisposable? StartProviderRequest(RuntimeEndpointId runtime, string providerKey)
    {
        var activity = providerSource.StartActivity(
            TelemetryNames.ActivitySources.ProviderRequest,
            ActivityKind.Client);

        activity?.SetTag(TelemetryNames.Attributes.RuntimeId, runtime.Value);
        activity?.SetTag(TelemetryNames.Attributes.ProviderType, providerKey);

        return activity;
    }

    /// <inheritdoc />
    public void RecordExchange(ExchangeTelemetrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var tags = Dimensions(snapshot);

        exchanges.Add(1, tags);
        exchangeDuration.Record(snapshot.TotalDuration.TotalMilliseconds, tags);

        if (snapshot.UpstreamDuration is { } upstream)
        {
            upstreamDuration.Record(upstream.TotalMilliseconds, tags);
        }

        if (snapshot.TimeToHeaders is { } headers)
        {
            timeToHeaders.Record(headers.TotalMilliseconds, tags);
        }

        // Recorded only when the runtime reported them. A zero would be a claim that no tokens were
        // consumed rather than a statement that AgentSplice does not know (FR-OBS-003).
        if (snapshot.PromptTokens is { } prompt)
        {
            promptTokens.Record(prompt.Value, tags);
        }

        if (snapshot.CompletionTokens is { } completion)
        {
            completionTokens.Record(completion.Value, tags);
        }
    }

    /// <inheritdoc />
    public void RecordDiscovery(RuntimeEndpointId runtime, TimeSpan duration) =>
        discoveryDuration.Record(
            duration.TotalMilliseconds,
            new KeyValuePair<string, object?>(TelemetryNames.Attributes.RuntimeId, runtime.Value));

    /// <inheritdoc />
    public void Dispose()
    {
        exchangeSource.Dispose();
        providerSource.Dispose();
        meter.Dispose();
    }

    /// <summary>
    /// Builds the dimension set for an exchange.
    /// </summary>
    /// <remarks>
    /// Success and failure are classified from the recorded upstream status class, never from the
    /// absence of an error type: a relayed upstream 500 is a completed transport cycle with no
    /// AgentSplice failure, and must not be counted as a success.
    /// </remarks>
    private static TagList Dimensions(ExchangeTelemetrySnapshot snapshot)
    {
        var tags = new TagList
        {
            { TelemetryNames.Attributes.IngressProtocol, snapshot.Protocol.ToString() },
            { TelemetryNames.Attributes.Streaming, snapshot.Streaming },
            { TelemetryNames.Attributes.ExchangeStatus, snapshot.Status.ToString() },
        };

        if (snapshot.Runtime is { } runtime)
        {
            tags.Add(TelemetryNames.Attributes.RuntimeId, runtime.Value);
        }

        if (snapshot.ProviderKey is { } providerKey)
        {
            tags.Add(TelemetryNames.Attributes.ProviderType, providerKey);
        }

        if (snapshot.UpstreamStatusClass is { } statusClass)
        {
            tags.Add(TelemetryNames.Attributes.UpstreamStatusClass, statusClass);
        }

        if (snapshot.ErrorType is { } errorType)
        {
            tags.Add(TelemetryNames.Attributes.ErrorType, errorType);
        }

        return tags;
    }

    private sealed class ExchangeTrace : IExchangeTrace
    {
        private readonly Activity? activity;
        private readonly UpDownCounter<long> activeExchanges;
        private bool disposed;

        internal ExchangeTrace(Activity? activity, UpDownCounter<long> activeExchanges)
        {
            this.activity = activity;
            this.activeExchanges = activeExchanges;

            TraceId = activity is not null
                && Domain.Identifiers.TraceId.TryCreate(activity.TraceId.ToHexString(), out var traceId)
                    ? traceId
                    : null;
        }

        public TraceId? TraceId { get; }

        public void SetRuntime(RuntimeEndpointId runtime, string providerKey)
        {
            activity?.SetTag(TelemetryNames.Attributes.RuntimeId, runtime.Value);
            activity?.SetTag(TelemetryNames.Attributes.ProviderType, providerKey);
        }

        public void SetOutcome(Domain.Exchanges.ExchangeStatus status, string? errorType)
        {
            activity?.SetTag(TelemetryNames.Attributes.ExchangeStatus, status.ToString());

            if (errorType is not null)
            {
                activity?.SetTag(TelemetryNames.Attributes.ErrorType, errorType);
                activity?.SetStatus(ActivityStatusCode.Error);
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            activeExchanges.Add(-1);
            activity?.Dispose();
        }
    }
}
