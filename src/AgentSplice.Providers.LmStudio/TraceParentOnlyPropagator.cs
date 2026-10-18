using System.Diagnostics;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Sends a runtime the gateway's own <c>traceparent</c>, and nothing a client chose (ADR 0027).
/// </summary>
/// <remarks>
/// <c>HttpClient</c> injects trace context from the current activity, and .NET's default propagator
/// writes <c>tracestate</c> and <c>baggage</c> beside <c>traceparent</c>. Both of those come from the
/// client's own request headers, by way of ASP.NET Core's request activity, so a runtime received text
/// a client chose, outside the header allowlist (docs/API.md). The gateway's <c>traceparent</c> names
/// the gateway's own span, so a runtime that records trace context can still be correlated with the
/// exchange.
/// </remarks>
internal sealed class TraceParentOnlyPropagator : DistributedContextPropagator
{
    internal const string TraceParent = "traceparent";

    private static readonly DistributedContextPropagator Inner = CreateDefaultPropagator();

    /// <inheritdoc />
    public override IReadOnlyCollection<string> Fields { get; } = [TraceParent];

    /// <inheritdoc />
    public override void Inject(Activity? activity, object? carrier, PropagatorSetterCallback? setter)
    {
        if (setter is null)
        {
            return;
        }

        Inner.Inject(activity, carrier, (target, name, value) =>
        {
            if (string.Equals(name, TraceParent, StringComparison.OrdinalIgnoreCase))
            {
                setter(target, name, value);
            }
        });
    }

    /// <inheritdoc />
    public override void ExtractTraceIdAndState(
        object? carrier,
        PropagatorGetterCallback? getter,
        out string? traceId,
        out string? traceState) =>
        Inner.ExtractTraceIdAndState(carrier, getter, out traceId, out traceState);

    /// <inheritdoc />
    public override IEnumerable<KeyValuePair<string, string?>>? ExtractBaggage(
        object? carrier,
        PropagatorGetterCallback? getter) =>
        Inner.ExtractBaggage(carrier, getter);
}
