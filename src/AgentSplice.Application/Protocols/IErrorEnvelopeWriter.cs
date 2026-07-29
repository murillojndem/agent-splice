using AgentSplice.Application.Errors;

namespace AgentSplice.Application.Protocols;

/// <summary>
/// Serialises a <see cref="GatewayError"/> in the ingress protocol's own error shape
/// (docs/SPECIFICATION.md section 10.3).
/// </summary>
public interface IErrorEnvelopeWriter
{
    /// <summary>The media type the written payload carries.</summary>
    string MediaType { get; }

    /// <summary>Writes the error as a UTF-8 payload.</summary>
    byte[] Write(GatewayError gatewayError);
}
