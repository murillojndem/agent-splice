using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using AgentSplice.Domain.Exchanges;

namespace AgentSplice.Infrastructure.Persistence;

/// <summary>
/// The serializer for the store's document-shaped columns.
/// </summary>
/// <remarks>
/// Source-generated and serialization-only. The structural summaries are written here and read back
/// by the administrative API as opaque documents — <c>ExchangeDetail.structuralSummary</c> is declared
/// <c>additionalProperties: true</c> precisely so a summary gains a field without a schema change — so
/// nothing needs to deserialize them into domain types, and declaring a read path would invite exactly
/// the round-trip that turns stored evidence into something rewritable.
///
/// camelCase because these documents are served to clients verbatim, and the rest of the
/// administrative contract is camelCase.
/// </remarks>
[JsonSourceGenerationOptions(
    GenerationMode = JsonSourceGenerationMode.Serialization,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StructuralRequestSummary))]
[JsonSerializable(typeof(StructuralResponseSummary))]
[JsonSerializable(typeof(PersistedUsage))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
internal sealed partial class PersistenceJsonContext : JsonSerializerContext
{
    /// <summary>Serializes a value, or returns <c>null</c> when there is nothing to store.</summary>
    /// <remarks>
    /// Returning <c>null</c> rather than <c>"null"</c> or <c>"{}"</c> keeps "there was no summary"
    /// distinguishable from "there was an empty one" at the column level, which is the same
    /// absent-versus-zero rule the measurements follow (FR-TRACE-006).
    /// </remarks>
    internal static string? Serialize<TValue>(TValue? value, JsonTypeInfo<TValue> typeInfo)
        where TValue : class =>
        value is null ? null : JsonSerializer.Serialize(value, typeInfo);
}
