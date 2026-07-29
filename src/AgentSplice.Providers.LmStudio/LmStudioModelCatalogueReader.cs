using System.Buffers;
using System.Text.Json;
using AgentSplice.Application.Runtimes;
using AgentSplice.Domain.Identifiers;

namespace AgentSplice.Providers.LmStudio;

/// <summary>
/// Reads an OpenAI-compatible <c>GET /v1/models</c> body into provider-neutral models.
/// </summary>
/// <remarks>
/// The only place LM Studio's response shape is understood. Unknown fields on a model object are
/// ignored rather than rejected: a runtime adding a field must not break discovery, and Stage 1 does
/// not claim to model anything beyond identity, creation time, and owner.
///
/// A malformed entry is skipped rather than failing the whole catalogue. One unusable model should
/// degrade discovery by one model, not empty a working runtime's inventory.
/// </remarks>
internal static class LmStudioModelCatalogueReader
{
    internal static RuntimeModelListResult Read(ReadOnlySequence<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);

            if (!JsonDocument.TryParseValue(ref reader, out var document))
            {
                return Invalid("body.unparsable");
            }

            using (document)
            {
                return Read(document.RootElement);
            }
        }
        catch (JsonException)
        {
            return Invalid("body.malformed");
        }
    }

    private static RuntimeModelListResult Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            return Invalid("body.not_an_object");
        }

        if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            return Invalid("data.missing");
        }

        var models = new List<DiscoveredModel>(data.GetArrayLength());

        foreach (var element in data.EnumerateArray())
        {
            if (TryReadModel(element, out var model))
            {
                models.Add(model);
            }
        }

        return RuntimeModelListResult.Success(models);
    }

    private static bool TryReadModel(JsonElement element, out DiscoveredModel model)
    {
        model = null!;

        if (element.ValueKind != JsonValueKind.Object
            || !element.TryGetProperty("id", out var id)
            || id.ValueKind != JsonValueKind.String
            || !UpstreamModelId.TryCreate(id.GetString(), out var modelId))
        {
            return false;
        }

        model = DiscoveredModel.Create(modelId, ReadCreated(element), ReadOwnedBy(element));
        return true;
    }

    /// <summary>
    /// Reads <c>created</c> only when the runtime actually reported a number.
    /// </summary>
    /// <remarks>
    /// An absent or non-numeric value yields <c>null</c>, never zero: zero is a Unix timestamp
    /// meaning 1970-01-01, so substituting it here would turn "not reported" into a fabricated
    /// creation date that later surfaces as evidence (FR-TRACE-006).
    /// </remarks>
    private static long? ReadCreated(JsonElement element) =>
        element.TryGetProperty("created", out var created)
        && created.ValueKind == JsonValueKind.Number
        && created.TryGetInt64(out var value)
            ? value
            : null;

    private static string? ReadOwnedBy(JsonElement element) =>
        element.TryGetProperty("owned_by", out var ownedBy) && ownedBy.ValueKind == JsonValueKind.String
            ? ownedBy.GetString()
            : null;

    private static RuntimeModelListResult Invalid(string detail) =>
        RuntimeModelListResult.Failed(UpstreamFailure.Create(
            UpstreamFailureReason.InvalidResponse,
            details: Domain.Observations.SafeDetails.Create("upstream.catalogue", detail)));
}
