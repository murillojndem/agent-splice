namespace AgentSplice.Application.Configuration;

/// <summary>
/// OTLP export settings (docs/OBSERVABILITY.md "OpenTelemetry export", ADR 0014).
/// </summary>
/// <remarks>
/// Off unless an operator turns it on, and the destination is never defaulted. Exported telemetry
/// carries no content, but it does describe every exchange — which runtime served it, how long each
/// phase took, how it ended — and a stream of that leaving the process is a decision rather than a
/// default. For the same reason <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> in the environment neither turns
/// export on nor redirects it: a variable left over from another tool is not consent.
/// </remarks>
public sealed class OtlpExportOptions
{
    /// <summary>Whether traces and metrics are exported at all.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Base address of the collector's OTLP receiver, such as <c>http://127.0.0.1:4318</c>.
    /// </summary>
    /// <remarks>
    /// For <see cref="TelemetryExportProtocol.HttpProtobuf"/> the signal paths <c>v1/traces</c> and
    /// <c>v1/metrics</c> are appended to it, after any path it already has; for
    /// <see cref="TelemetryExportProtocol.Grpc"/> it is the receiver's address and carries no path.
    /// </remarks>
    public string? Endpoint { get; set; }

    /// <summary>The OTLP transport.</summary>
    public TelemetryExportProtocol Protocol { get; set; } = TelemetryExportProtocol.HttpProtobuf;
}
