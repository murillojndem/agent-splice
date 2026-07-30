using System.Text.Json;

namespace AgentSplice.Protocols.OpenAI.ChatCompletions;

/// <summary>
/// One pass over a completion request body, gathering structure without reading content.
/// </summary>
/// <remarks>
/// A reader rather than a document, for two reasons.
///
/// It never materialises message text. <c>JsonDocument</c> would hold every prompt string in memory
/// as a parsed value, which is exactly the content Stage 1 is built not to retain.
///
/// And it can report byte offsets. The span of the top-level <c>model</c> value is what allows the
/// forwarded body to be the client's original bytes with only that one value replaced. Re-emitting a
/// parsed document would be semantically equivalent but not byte-identical — a writer normalises
/// escape forms and number formatting — so an exact-forwarding test built on it would only prove our
/// own parser round-trips.
/// </remarks>
internal static class ChatCompletionRequestScanner
{
    internal static ChatCompletionRequestScan Scan(ReadOnlySpan<byte> body)
    {
        var scan = new ChatCompletionRequestScan();
        var reader = new Utf8JsonReader(body, isFinalBlock: true, state: default);

        try
        {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                scan.Failure = reader.TokenType == JsonTokenType.None
                    ? ScanFailure.MalformedJson
                    : ScanFailure.NotAnObject;
                return scan;
            }

            ReadTopLevel(ref reader, body, scan);
        }
        catch (JsonException)
        {
            scan.Failure = ScanFailure.MalformedJson;
            return scan;
        }

        return scan;
    }

    private static void ReadTopLevel(ref Utf8JsonReader reader, ReadOnlySpan<byte> body, ChatCompletionRequestScan scan)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                scan.Failure = ScanFailure.MalformedJson;
                return;
            }

            var name = reader.GetString()!;

            // A repeated field that drives behaviour is unarguably a defect: "last wins" differs
            // between our validation, the splice arithmetic, and the runtime's own parser, so the
            // three could disagree about what was actually sent.
            if (!seen.Add(name) && IsBehavioural(name))
            {
                scan.Failure = ScanFailure.DuplicateField;
                scan.FailedField = name;
                return;
            }

            if (!reader.Read())
            {
                scan.Failure = ScanFailure.MalformedJson;
                return;
            }

            ReadValue(ref reader, body, scan, name);

            if (scan.Failure != ScanFailure.None)
            {
                return;
            }
        }

        // Absence is only detectable once the object has been walked: the loop validates what it
        // encounters, and a required field that never appears is encountered nowhere.
        RequirePresent(scan, seen, KnownChatCompletionFields.Model, ScanFailure.ModelMissing);
        RequirePresent(scan, seen, KnownChatCompletionFields.Messages, ScanFailure.MessagesMissing);
    }

    private static void RequirePresent(
        ChatCompletionRequestScan scan,
        HashSet<string> seen,
        string name,
        ScanFailure failure)
    {
        if (scan.Failure == ScanFailure.None && !seen.Contains(name))
        {
            scan.Failure = failure;
            scan.FailedField = name;
        }
    }

    private static void ReadValue(
        ref Utf8JsonReader reader,
        ReadOnlySpan<byte> body,
        ChatCompletionRequestScan scan,
        string name)
    {
        switch (name)
        {
            case KnownChatCompletionFields.Model:
                ReadModel(ref reader, body, scan);
                return;

            case KnownChatCompletionFields.Messages:
                ReadMessages(ref reader, scan);
                return;

            case KnownChatCompletionFields.Stream:
                ReadStream(ref reader, scan);
                return;

            case KnownChatCompletionFields.Tools:
                ReadTools(ref reader, scan);
                return;

            case KnownChatCompletionFields.ToolChoice:
                scan.ToolChoicePresent = reader.TokenType != JsonTokenType.Null;
                reader.Skip();
                return;

            case KnownChatCompletionFields.StreamOptions:
                scan.StreamOptionsPresent = reader.TokenType != JsonTokenType.Null;
                reader.Skip();
                return;

            default:
                if (!KnownChatCompletionFields.TopLevel.Contains(name))
                {
                    scan.UnknownFieldNames.Add(name);
                }

                reader.Skip();
                return;
        }
    }

    private static void ReadModel(ref Utf8JsonReader reader, ReadOnlySpan<byte> body, ChatCompletionRequestScan scan)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            scan.Failure = ScanFailure.ModelMissing;
            scan.FailedField = KnownChatCompletionFields.Model;
            reader.Skip();
            return;
        }

        scan.Model = reader.GetString();

        // The span covers the quoted literal exactly as it appears in the body: opening quote, the
        // raw bytes including any escape sequences, closing quote. BytesConsumed is not usable here
        // because it can include whitespace the reader passed over on the way to the next token.
        var start = checked((int)reader.TokenStartIndex);
        var end = FindClosingQuote(body, start);

        if (end < 0)
        {
            scan.Failure = ScanFailure.MalformedJson;
            return;
        }

        scan.ModelValueStart = start;
        scan.ModelValueEnd = end;
    }

    /// <summary>
    /// Finds the end of a JSON string literal that starts at <paramref name="start"/>.
    /// </summary>
    /// <remarks>
    /// Walks the raw bytes rather than trusting a decoded length, because an escape sequence makes
    /// the encoded literal longer than its value. A backslash escapes the next byte, so a quote
    /// preceded by one does not terminate the string. Working in bytes is safe for UTF-8: every
    /// continuation byte has its high bit set and can never be mistaken for a quote or a backslash.
    /// </remarks>
    private static int FindClosingQuote(ReadOnlySpan<byte> body, int start)
    {
        const byte Quote = (byte)'"';
        const byte Backslash = (byte)'\\';

        if (start < 0 || start >= body.Length || body[start] != Quote)
        {
            return -1;
        }

        for (var index = start + 1; index < body.Length; index++)
        {
            if (body[index] == Backslash)
            {
                index++;
                continue;
            }

            if (body[index] == Quote)
            {
                return index + 1;
            }
        }

        return -1;
    }

    private static void ReadMessages(ref Utf8JsonReader reader, ChatCompletionRequestScan scan)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            scan.Failure = ScanFailure.MessagesMissing;
            scan.FailedField = KnownChatCompletionFields.Messages;
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            scan.MessageCount++;
            CountRole(ref reader, scan);
        }

        if (scan.MessageCount == 0)
        {
            scan.Failure = ScanFailure.MessagesEmpty;
            scan.FailedField = KnownChatCompletionFields.Messages;
        }
    }

    /// <summary>
    /// Records which role a message declared, reading nothing else about it.
    /// </summary>
    /// <remarks>
    /// A role is a protocol token from a small vocabulary, so counting occurrences answers "what
    /// kind of conversation was this" without retaining a single character of what was said.
    /// </remarks>
    private static void CountRole(ref Utf8JsonReader reader, ChatCompletionRequestScan scan)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            Increment(scan, Domain.Exchanges.StructuralRequestSummary.UnspecifiedRoleName);
            return;
        }

        string? role = null;

        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName)
            {
                continue;
            }

            var isRole = reader.ValueTextEquals("role");

            if (!reader.Read())
            {
                break;
            }

            if (isRole && reader.TokenType == JsonTokenType.String)
            {
                role ??= reader.GetString();
            }
            else
            {
                reader.Skip();
            }
        }

        Increment(scan, role ?? Domain.Exchanges.StructuralRequestSummary.UnspecifiedRoleName);
    }

    private static void ReadStream(ref Utf8JsonReader reader, ChatCompletionRequestScan scan)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                scan.StreamRequested = true;
                return;

            case JsonTokenType.False:
            case JsonTokenType.Null:
                return;

            default:
                // Not merely odd: a truthy non-boolean such as "true" would be forwarded, the
                // runtime would answer with an event stream, and the non-streaming path would then
                // buffer it — silently turning a stream into a single JSON body.
                scan.Failure = ScanFailure.StreamNotBoolean;
                scan.FailedField = KnownChatCompletionFields.Stream;
                reader.Skip();
                return;
        }
    }

    private static void ReadTools(ref Utf8JsonReader reader, ChatCompletionRequestScan scan)
    {
        if (reader.TokenType != JsonTokenType.StartArray)
        {
            // Recorded, not rejected. Only model and messages are required by the schema, and the
            // runtime is the authority on everything else.
            scan.ToolsMalformed = reader.TokenType != JsonTokenType.Null;
            reader.Skip();
            return;
        }

        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            scan.ToolDeclarationCount++;
            reader.Skip();
        }
    }

    private static void Increment(ChatCompletionRequestScan scan, string role) =>
        scan.RoleCounts[role] = scan.RoleCounts.GetValueOrDefault(role) + 1;

    private static bool IsBehavioural(string name) =>
        name is KnownChatCompletionFields.Model
            or KnownChatCompletionFields.Messages
            or KnownChatCompletionFields.Stream;
}
