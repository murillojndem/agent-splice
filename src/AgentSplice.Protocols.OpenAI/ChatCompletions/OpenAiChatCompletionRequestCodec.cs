using System.Text.Json;
using AgentSplice.Application.Configuration;
using AgentSplice.Application.Errors;
using AgentSplice.Application.Protocols;
using AgentSplice.Domain.Exchanges;
using AgentSplice.Domain.Identifiers;
using Microsoft.Extensions.Options;

namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>
/// Reads and rewrites OpenAI chat completion requests (docs/SPECIFICATION.md FR-CHAT-001,
/// FR-CHAT-004).
/// </summary>
public sealed class OpenAiChatCompletionRequestCodec : IChatCompletionRequestCodec
{
    private readonly IOptions<AgentSpliceOptions> options;

    /// <summary>Creates the codec.</summary>
    public OpenAiChatCompletionRequestCodec(IOptions<AgentSpliceOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.options = options;
    }

    /// <inheritdoc />
    public ChatCompletionReadResult Read(ReadOnlySpan<byte> body)
    {
        var scan = ChatCompletionRequestScanner.Scan(body);

        if (scan.Failure != ScanFailure.None)
        {
            return ChatCompletionReadResult.Invalid(Describe(scan));
        }

        if (!ClientModelId.TryCreate(scan.Model, out var model))
        {
            return ChatCompletionReadResult.Invalid(InvalidRequest(
                "The 'model' field is not a usable model identifier.",
                KnownChatCompletionFields.Model));
        }

        // FR-CHAT-005: the policy is explicit, so a deployment that would rather fail loudly than
        // forward the unfamiliar can say so. Only top-level names are considered, because those are
        // the only ones AgentSplice claims to understand.
        if (options.Value.Compatibility.UnsupportedFields == CompatibilityMode.Strict
            && scan.UnknownFieldNames.Count > 0)
        {
            return ChatCompletionReadResult.Invalid(InvalidRequest(
                "The request carries a field this gateway does not model, and the compatibility policy is strict.",
                scan.UnknownFieldNames[0]));
        }

        var summary = StructuralRequestSummary.Create(
            scan.MessageCount,
            scan.RoleCounts,
            scan.ToolDeclarationCount,
            scan.ToolChoicePresent,
            scan.StreamRequested,
            scan.StreamOptionsPresent,
            body.Length,
            scan.UnknownFieldNames,

            // Nothing is ever dropped on the Stage 1A path. The empty list is the positive evidence
            // that forwarding was transparent (FR-TRACE-008).
            droppedFieldNames: null);

        return ChatCompletionReadResult.Valid(ChatCompletionEnvelope.Create(
            model,
            scan.StreamRequested,
            summary,
            scan.ModelValueStart,
            scan.ModelValueEnd));
    }

    /// <inheritdoc />
    public byte[] SubstituteModel(
        ReadOnlySpan<byte> body,
        ChatCompletionEnvelope envelope,
        UpstreamModelId model)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        if (model.IsEmpty)
        {
            throw new ArgumentException("Substitution requires an upstream model.", nameof(model));
        }

        if (envelope.ModelValueEnd > body.Length)
        {
            throw new ArgumentException(
                "The envelope describes a model value outside the supplied body.",
                nameof(body));
        }

        // Encoded rather than copied: a model identifier is an opaque third-party value and may
        // legitimately contain a quote or a backslash, which spliced in raw would produce a
        // malformed document.
        var replacement = EncodeJsonString(model.Value);

        var prefix = body[..envelope.ModelValueStart];
        var suffix = body[envelope.ModelValueEnd..];
        var result = new byte[prefix.Length + replacement.Length + suffix.Length];

        prefix.CopyTo(result);
        replacement.CopyTo(result.AsSpan(prefix.Length));
        suffix.CopyTo(result.AsSpan(prefix.Length + replacement.Length));

        return result;
    }

    private static byte[] EncodeJsonString(string value)
    {
        using var buffer = new MemoryStream();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStringValue(value);
        }

        return buffer.ToArray();
    }

    private static GatewayError Describe(ChatCompletionRequestScan scan) => scan.Failure switch
    {
        ScanFailure.MalformedJson => InvalidRequest("The request body is not valid JSON."),
        ScanFailure.NotAnObject => InvalidRequest("The request body must be a JSON object."),
        ScanFailure.ModelMissing => InvalidRequest(
            "The 'model' field is required and must be a string.",
            KnownChatCompletionFields.Model),
        ScanFailure.ModelInvalid => InvalidRequest(
            "The 'model' field is not a usable model identifier.",
            KnownChatCompletionFields.Model),
        ScanFailure.MessagesMissing => InvalidRequest(
            "The 'messages' field is required and must be an array.",
            KnownChatCompletionFields.Messages),
        ScanFailure.MessagesEmpty => InvalidRequest(
            "The 'messages' array must contain at least one message.",
            KnownChatCompletionFields.Messages),
        ScanFailure.StreamNotBoolean => InvalidRequest(
            "The 'stream' field must be a boolean.",
            KnownChatCompletionFields.Stream),
        ScanFailure.DuplicateField => InvalidRequest(
            "A field that determines how the request is handled was supplied more than once.",
            scan.FailedField),
        _ => InvalidRequest("The request body is not valid for this endpoint."),
    };

    private static GatewayError InvalidRequest(string message, string? param = null) =>
        GatewayError.Create(
            ErrorCodes.InvalidRequest,
            ErrorTypes.InvalidRequest,
            400,
            message,
            param,
            FailureClass.InvalidRequest);
}
