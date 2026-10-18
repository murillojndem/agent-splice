using System.Text.Json;

namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>
/// Recognises a runtime reporting an error in the OpenAI protocol's own place for one (ADR 0026).
/// </summary>
/// <remarks>
/// The place is a top-level <c>error</c> member, which is what an OpenAI-compatible runtime sends in
/// place of a completion: inside a <c>200</c> stream when it cannot answer, and as the envelope of a
/// non-2xx answer. Any value but <c>null</c> or <c>false</c> counts, because that is what clients
/// act on: the OpenAI SDKs raise on a chunk whose <c>error</c> is present and truthy, a string
/// included, and the AI SDK's OpenAI-compatible provider rejects a chunk carrying one. Accepting only
/// an object would have relied on LM Studio sending one, and no capture of its bytes exists to show
/// which it sends.
///
/// Structure only, as <c>[DONE]</c> and <c>usage</c> are recognised. Nothing in the member is read:
/// its message can echo the prompt or a template, and recording it would put content in the evidence.
/// A null or false <c>error</c>, and the text "error" anywhere in model output, are not a report, and
/// nothing under <c>choices</c> is ever consulted.
/// </remarks>
internal static class OpenAiErrorShape
{
    /// <summary>True when the object carries a top-level <c>error</c> that is neither null nor false.</summary>
    internal static bool Reports(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("error", out var error)
        && error.ValueKind is not (JsonValueKind.Null or JsonValueKind.False or JsonValueKind.Undefined);
}
