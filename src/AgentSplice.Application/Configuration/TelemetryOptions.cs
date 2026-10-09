namespace AgentSplice.Application.Configuration;

/// <summary>
/// Where spans and metrics go once they leave the process (docs/OBSERVABILITY.md
/// "OpenTelemetry export", ADR 0014).
/// </summary>
public sealed class TelemetryOptions
{
    /// <summary>OTLP export of traces and metrics.</summary>
    public OtlpExportOptions Otlp { get; set; } = new();
}
