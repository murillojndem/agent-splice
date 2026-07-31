using AgentSplice.Domain.Exchanges;

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
    /// Two mistakes to avoid, and they are opposites. Whole-string equality misreads a conforming
    /// <c>text/event-stream; charset=utf-8</c> as something else and sends a valid stream down the
    /// buffered path. Splitting on the first semicolon and ignoring the rest accepts
    /// <c>text/event-stream; ===</c>, which is not a media type at all — a classifier claiming to
    /// recognise a header it never read.
    ///
    /// The grammar itself is HTTP rather than OpenAI, so it lives in
    /// <see cref="MediaTypeGrammar"/>. What belongs here is only the last step: whether the media
    /// type it found is this protocol's (ADR 0012).
    /// </remarks>
    public static bool IsEventStream(string? contentType) =>
        string.Equals(MediaTypeGrammar.Parse(contentType), EventStream, StringComparison.Ordinal);
}
