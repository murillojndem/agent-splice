using AgentSplice.Application.Configuration;

namespace AgentSplice.Observability;

/// <summary>
/// Where traces and metrics are sent, resolved from validated configuration.
/// </summary>
/// <param name="Protocol">The OTLP transport.</param>
/// <param name="Endpoint">The receiver's base address, as configured.</param>
/// <param name="TracesEndpoint">The address spans are posted to.</param>
/// <param name="MetricsEndpoint">The address metrics are posted to.</param>
/// <remarks>
/// The signal addresses are computed here rather than left to the SDK. The SDK appends
/// <c>v1/traces</c> to an HTTP endpoint only when it chose the endpoint itself; once one is set in
/// code it is used verbatim, so a base address handed straight to it would post spans to the
/// receiver's root and be answered 404 — and an exporter failure never reaches anything an operator
/// watches.
/// </remarks>
public sealed record OtlpExportTarget(
    TelemetryExportProtocol Protocol,
    Uri Endpoint,
    Uri TracesEndpoint,
    Uri MetricsEndpoint)
{
    /// <summary>
    /// Resolves the export target, or <c>null</c> when export is off.
    /// </summary>
    /// <remarks>
    /// Assumes validated options: startup validation has already refused an enabled export with no
    /// endpoint, so a missing one here is a composition fault rather than a configuration choice.
    /// </remarks>
    public static OtlpExportTarget? From(OtlpExportOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!options.Enabled)
        {
            return null;
        }

        var endpoint = new Uri(
            options.Endpoint ?? throw new InvalidOperationException("OTLP export is enabled without an endpoint; startup validation should have refused this."),
            UriKind.Absolute);

        if (options.Protocol == TelemetryExportProtocol.Grpc)
        {
            // gRPC addresses a service on the receiver; the SDK supplies the method path.
            return new OtlpExportTarget(options.Protocol, endpoint, endpoint, endpoint);
        }

        // A base address with a path keeps it: a collector behind a reverse proxy at /otlp receives
        // spans at /otlp/v1/traces. Without the trailing slash, relative resolution would replace the
        // last segment instead of appending to it.
        var baseAddress = endpoint.AbsoluteUri.EndsWith('/')
            ? endpoint
            : new Uri(endpoint.AbsoluteUri + "/", UriKind.Absolute);

        return new OtlpExportTarget(
            options.Protocol,
            endpoint,
            new Uri(baseAddress, "v1/traces"),
            new Uri(baseAddress, "v1/metrics"));
    }
}
