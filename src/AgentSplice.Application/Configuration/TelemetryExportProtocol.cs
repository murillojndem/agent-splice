namespace AgentSplice.Application.Configuration;

/// <summary>The OTLP transport a collector is reached over.</summary>
/// <remarks>
/// <see cref="HttpProtobuf"/> is the default because the OpenTelemetry specification recommends it as
/// the SDK default and it needs nothing beyond HTTP/1.1 between the gateway and the collector. The
/// .NET SDK's own default is gRPC for historical reasons, which is exactly why this is an explicit
/// AgentSplice setting rather than something inherited from the SDK.
/// </remarks>
public enum TelemetryExportProtocol
{
    /// <summary>Protobuf over HTTP, conventionally port 4318.</summary>
    HttpProtobuf = 0,

    /// <summary>gRPC, conventionally port 4317.</summary>
    Grpc = 1,
}
