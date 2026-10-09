using System.Text;

namespace AgentSplice.TestSupport.Otlp;

/// <summary>One export call as the fake collector received it.</summary>
/// <param name="Path">The request path: an OTLP/HTTP signal path or a gRPC method.</param>
/// <param name="Protocol">The HTTP version the exporter used, such as <c>HTTP/2</c>.</param>
/// <param name="ContentType">The request's content type.</param>
/// <param name="Body">The raw payload, as sent.</param>
public sealed record ReceivedOtlpExport(
    string Path,
    string Protocol,
    string? ContentType,
    ReadOnlyMemory<byte> Body)
{
    /// <summary>Whether the payload carries <paramref name="text"/> as UTF-8.</summary>
    public bool Contains(string text) => Contains(Encoding.UTF8.GetBytes(text));

    /// <summary>Whether the payload carries <paramref name="bytes"/> verbatim.</summary>
    /// <remarks>For binary fields such as trace identifiers, which OTLP encodes as raw bytes.</remarks>
    public bool Contains(ReadOnlySpan<byte> bytes) => Body.Span.IndexOf(bytes) >= 0;
}
