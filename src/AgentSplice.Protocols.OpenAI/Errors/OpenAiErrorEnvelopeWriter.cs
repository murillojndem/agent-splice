using System.Text.Json;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;

namespace AgentSplice.Protocols.OpenAI.Errors;

/// <summary>
/// Writes the OpenAI-shaped error envelope (docs/SPECIFICATION.md section 10.3).
/// </summary>
/// <remarks>
/// <c>param</c> is written as an explicit null rather than omitted when no field can be named,
/// because the documented example includes it and a client deserialising into a fixed shape should
/// not see the property appear and disappear.
/// </remarks>
public sealed class OpenAiErrorEnvelopeWriter : IErrorEnvelopeWriter
{
    /// <inheritdoc />
    public string MediaType => OpenAiMediaTypes.Json;

    /// <inheritdoc />
    public byte[] Write(GatewayError gatewayError)
    {
        ArgumentNullException.ThrowIfNull(gatewayError);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartObject("error");
            writer.WriteString("message", gatewayError.Message);
            writer.WriteString("type", gatewayError.Type);

            if (gatewayError.Param is { } param)
            {
                writer.WriteString("param", param);
            }
            else
            {
                writer.WriteNull("param");
            }

            writer.WriteString("code", gatewayError.Code);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
