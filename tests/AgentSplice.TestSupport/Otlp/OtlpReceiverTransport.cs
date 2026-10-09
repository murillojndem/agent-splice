namespace AgentSplice.TestSupport.Otlp;

/// <summary>Which OTLP transport the fake collector speaks.</summary>
public enum OtlpReceiverTransport
{
    /// <summary>OTLP/HTTP with protobuf payloads over HTTP/1.1.</summary>
    HttpProtobuf,

    /// <summary>OTLP/gRPC over cleartext HTTP/2.</summary>
    Grpc,
}
