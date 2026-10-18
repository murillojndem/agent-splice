using System.Text.Json;

namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>
/// Recognises a runtime reporting an error in the OpenAI protocol's own shape (ADR 0026).
/// </summary>
/// <remarks>
/// The shape is a top-level <c>error</c> member whose value is an object, which is what an
/// OpenAI-compatible runtime sends in place of a completion — LM Studio inside a <c>200</c> stream when
/// it cannot render a chat template, and the error envelope of any non-2xx answer. It is what the
/// OpenAI SDKs and the AI SDK's OpenAI-compatible provider read as an error.
///
/// Structure only, as <c>[DONE]</c> and <c>usage</c> are recognised. Nothing inside the object is
/// read: its message can echo the prompt or a template, and recording it would put content in the
/// evidence. A null <c>error</c>, a string, and the text "error" anywhere in model output are not this
/// shape, and nothing under <c>choices</c> is ever consulted.
/// </remarks>
internal static class OpenAiErrorShape
{
    /// <summary>True when the object carries a top-level <c>error</c> object.</summary>
    internal static bool Reports(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("error", out var error)
        && error.ValueKind == JsonValueKind.Object;
}
