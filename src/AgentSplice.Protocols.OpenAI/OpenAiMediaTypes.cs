namespace AgentSplice.Protocols.OpenAI;

/// <summary>Media types the OpenAI-compatible surface uses.</summary>
public static class OpenAiMediaTypes
{
    /// <summary>Request and response bodies on the non-streaming path.</summary>
    public const string Json = "application/json";

    /// <summary>Streamed responses, and what a streaming request asks the runtime for.</summary>
    public const string EventStream = "text/event-stream";

    /// <summary>
    /// True when a <c>Content-Type</c> value names the event-stream media type.
    /// </summary>
    /// <remarks>
    /// Matching by whole-string equality is the classic way a proxy misreads a conforming runtime:
    /// <c>text/event-stream; charset=utf-8</c> is the same media type as <c>text/event-stream</c>,
    /// and RFC 9110 makes the type and subtype case-insensitive. Getting this wrong sends a valid
    /// event stream down the buffered path, where it produces no SSE timeline and the wrong
    /// termination semantics — silently, because the bytes still reach the client.
    ///
    /// Parameters are ignored rather than parsed. Nothing in Stage 1 turns on a <c>charset</c>, and
    /// a runtime-supplied header is untrusted text: this may not throw on it, so it does the least
    /// work that answers the question.
    /// </remarks>
    public static bool IsEventStream(string? contentType) => Matches(contentType, EventStream);

    private static bool Matches(string? contentType, string mediaType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var value = contentType.AsSpan();
        var separator = value.IndexOf(';');
        var token = separator < 0 ? value : value[..separator];

        return token.Trim().Equals(mediaType, StringComparison.OrdinalIgnoreCase);
    }
}
