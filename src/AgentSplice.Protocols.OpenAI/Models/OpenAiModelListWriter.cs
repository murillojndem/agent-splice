using System.Text.Json;
using AgentSplice.Application.Models;
using AgentSplice.Application.Protocols;

namespace AgentSplice.Protocols.OpenAI.Models;

/// <summary>
/// Writes an OpenAI-compatible <c>GET /v1/models</c> response (docs/SPECIFICATION.md FR-MOD-001).
/// </summary>
/// <remarks>
/// The one place a compatibility sentinel is allowed to appear. The OpenAI <c>Model</c> schema marks
/// <c>created</c> required and integral, and mainstream SDKs deserialise it into a non-nullable
/// integer, so an entry with no creation evidence cannot omit the field or send null. It sends
/// <see cref="UnknownCreatedSentinel"/>.
///
/// That substitution happens here and nowhere else. <see cref="ModelCatalogueEntry.Created"/> stays
/// <c>null</c>, so the sentinel is never persisted, never compared, and never re-read as though it
/// were a date — which matters because zero is a real Unix timestamp meaning 1970-01-01, not a way
/// of saying "unknown" (FR-DASH-006). The honest value is exposed by the administrative catalogue,
/// which is not bound by this schema.
/// </remarks>
public sealed class OpenAiModelListWriter : IModelListWriter
{
    /// <summary>
    /// Emitted for <c>created</c> when nothing reported a creation time. A protocol placeholder, not
    /// a claim about the model.
    /// </summary>
    public const long UnknownCreatedSentinel = 0;

    /// <summary>Emitted for <c>owned_by</c> when nothing reported an owner.</summary>
    public const string UnknownOwner = "agentsplice";

    /// <inheritdoc />
    public string MediaType => OpenAiMediaTypes.Json;

    /// <inheritdoc />
    public byte[] Write(IReadOnlyList<ModelCatalogueEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("object", "list");
            writer.WriteStartArray("data");

            foreach (var entry in entries)
            {
                writer.WriteStartObject();
                writer.WriteString("id", entry.ClientModel.Value);
                writer.WriteString("object", "model");
                writer.WriteNumber("created", entry.Created ?? UnknownCreatedSentinel);
                writer.WriteString("owned_by", entry.OwnedBy ?? UnknownOwner);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
