namespace AgentSplice.Domain.Exchanges;

/// <summary>
/// What a runtime's response headers said, independently of whether its body could be interpreted.
/// </summary>
/// <remarks>
/// Kept separate from <see cref="StructuralResponseSummary"/> because status is transport metadata
/// while the summary describes an interpretable body, and the two genuinely come apart: a 204 has no
/// body, a <c>429 text/plain</c> has one that is not protocol data, and a truncated 500 has one that
/// cannot be parsed. In each case the status was observed and must be recordable, so attaching it to
/// the summary would lose it exactly when it matters most.
/// </remarks>
public sealed record UpstreamResponseMetadata
{
    /// <summary>Maximum retained length of an upstream request identifier.</summary>
    public const int MaxRequestIdLength = 128;

    /// <summary>
    /// Longest content type that will be written back to a client.
    /// </summary>
    /// <remarks>
    /// Far above the evidence bound and enforced by refusal rather than truncation, because the two
    /// bounds protect different things. Evidence is trimmed to keep a trace attribute small; a
    /// response header trimmed in the middle of a quoted parameter or a multipart boundary is not a
    /// shorter header, it is a different and broken one (ADR 0011).
    /// </remarks>
    public const int MaxRelayableContentTypeLength = 1024;

    private UpstreamResponseMetadata()
    {
    }

    /// <summary>The status the runtime returned.</summary>
    public int StatusCode { get; private init; }

    /// <summary>The media type only, with parameters stripped, or <c>null</c> when none was sent.</summary>
    /// <remarks>
    /// Evidence, and only evidence. It is produced by taking the text before the first semicolon, so
    /// it records what the runtime appeared to say even when the header as a whole was malformed —
    /// which is exactly when an operator most wants to see it. It is <em>not</em> proof that the
    /// header was valid, and nothing may classify a response from it:
    /// <see cref="ParsedMediaType"/> exists for that.
    /// </remarks>
    public string? ContentType { get; private init; }

    /// <summary>
    /// The media type a well-formed <c>Content-Type</c> named, or <c>null</c> when none was sent or
    /// the header did not conform to RFC 9110.
    /// </summary>
    /// <remarks>
    /// The value protocol classification asks about, and deliberately not
    /// <see cref="RelayableContentType"/>. Relayability answers "may this header be written to the
    /// client", which is a transport question with its own length and safety limits; whether the body
    /// is an event stream is a protocol question and must not inherit those limits. Classifying from
    /// the relayable value made a conforming stream whose header was merely too long to forward come
    /// out as a buffered response — no SSE parsing, no terminator handling, and an exchange recording
    /// "not streamed" while the client was reading events (ADR 0012).
    ///
    /// Parsed at construction, from the header as received, before either of the other two values has
    /// discarded anything.
    /// </remarks>
    public string? ParsedMediaType { get; private init; }

    /// <summary>
    /// The content type as the runtime sent it, parameters included, or <c>null</c> when it sent
    /// none or sent one that must not be written back.
    /// </summary>
    /// <remarks>
    /// Kept alongside <see cref="ContentType"/> rather than instead of it, because the two answer
    /// different questions and are bounded for different reasons. <see cref="ContentType"/> is the
    /// short token every decision and every trace attribute turns on; this is what gets written back
    /// to the client, and rewriting it would be a semantic transformation of the runtime's own answer
    /// — dropping a <c>charset</c> a client decodes by, or a <c>boundary</c> without which a body
    /// cannot be parsed at all.
    ///
    /// Validated, never repaired. A value carrying a control character or exceeding
    /// <see cref="MaxRelayableContentTypeLength"/> is refused outright and this stays <c>null</c>, so
    /// the relay falls back to the normalised token rather than forwarding a header that was
    /// truncated mid-parameter. Running it through the evidence sanitiser instead — which trims,
    /// substitutes, and truncates — produced a value the documentation called verbatim and was not.
    ///
    /// **This value must never reach a log, a span attribute, or <c>SafeDetails</c>.** It is
    /// unbounded-by-evidence-standards runtime text whose only destination is the wire;
    /// <see cref="ContentType"/> is what evidence records.
    /// </remarks>
    public string? RelayableContentType { get; private init; }

    /// <summary>The runtime's own request identifier, when it sent one (FR-CHAT-010).</summary>
    public string? UpstreamRequestId { get; private init; }

    /// <summary>When the response headers were observed.</summary>
    public DateTimeOffset HeadersReceivedAt { get; private init; }

    /// <summary>True when the runtime reported success.</summary>
    public bool IsSuccess => StatusCode is >= 200 and <= 299;

    /// <summary>
    /// The coarse status class, as a bounded token suitable for a metric dimension (FR-OBS-006).
    /// </summary>
    /// <remarks>
    /// Success and failure are classified from this rather than from the absence of a failure class,
    /// because a relayed upstream 500 is a completed transport cycle with no AgentSplice failure and
    /// must still never be counted as a success.
    /// </remarks>
    public string StatusClass => StatusCode switch
    {
        >= 200 and <= 299 => "2xx",
        >= 300 and <= 399 => "3xx",
        >= 400 and <= 499 => "4xx",
        >= 500 and <= 599 => "5xx",
        _ => "other",
    };

    /// <summary>Creates validated upstream response metadata.</summary>
    public static UpstreamResponseMetadata Create(
        int statusCode,
        DateTimeOffset headersReceivedAt,
        string? contentType = null,
        string? upstreamRequestId = null)
    {
        if (statusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                statusCode,
                "An HTTP status code must be in the range 100 to 599.");
        }

        return new UpstreamResponseMetadata
        {
            StatusCode = statusCode,
            ContentType = NormaliseMediaType(contentType),
            ParsedMediaType = MediaTypeGrammar.Parse(contentType),
            RelayableContentType = Relayable(contentType),
            UpstreamRequestId = Bound(upstreamRequestId, MaxRequestIdLength),
            HeadersReceivedAt = headersReceivedAt,
        };
    }

    /// <summary>
    /// Keeps the media type and discards its parameters.
    /// </summary>
    /// <remarks>
    /// A <c>charset</c> or <c>boundary</c> parameter is runtime-chosen text of unbounded length that
    /// would otherwise reach a trace attribute; the media type alone is what any decision here turns
    /// on.
    /// </remarks>
    private static string? NormaliseMediaType(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var separator = contentType.IndexOf(';', StringComparison.Ordinal);
        var mediaType = separator < 0 ? contentType : contentType[..separator];

        return Bound(mediaType.Trim().ToLowerInvariant(), 128);
    }

    /// <summary>
    /// Accepts a content type for relaying to the client, or refuses it.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="Bound"/>. That method exists to make a runtime string safe to put
    /// in a trace, and every one of its transformations is wrong here: truncating at 256 characters
    /// can cut a quoted parameter or a multipart boundary in half, and substituting a control
    /// character produces a header the runtime never sent while leaving it looking valid.
    ///
    /// Trimming is not one of those transformations. RFC 9110 section 5.5 excludes leading and
    /// trailing whitespace from a field value, so removing it recovers the value rather than
    /// altering it.
    ///
    /// A control character means refusal rather than repair: <c>CR</c> or <c>LF</c> in a header value
    /// is a response-splitting attempt, and the honest answer to one is to relay the normalised media
    /// type instead of whatever the runtime was trying to smuggle.
    /// </remarks>
    private static string? Relayable(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return null;
        }

        var trimmed = contentType.Trim();

        if (trimmed.Length > MaxRelayableContentTypeLength)
        {
            return null;
        }

        foreach (var character in trimmed)
        {
            // Horizontal tab excepted: RFC 9110 permits it inside a field value as optional
            // whitespace, so `text/event-stream\t;\tcharset=utf-8` is legal and refusing it would
            // cost fidelity for no security gain. Every other control character is refused.
            if (char.IsControl(character) && character != '\t')
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Bounds and sanitises a runtime-supplied string.
    /// </summary>
    /// <remarks>
    /// These values are echoed into observations and, in the case of the request identifier, are
    /// correlation data an operator will read. They are untrusted text, so control characters are
    /// replaced rather than carried into a log or a trace attribute.
    /// </remarks>
    private static string? Bound(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        var length = Math.Min(trimmed.Length, maxLength);
        var builder = new System.Text.StringBuilder(length);

        for (var index = 0; index < length; index++)
        {
            var character = trimmed[index];
            builder.Append(char.IsControl(character) ? ' ' : character);
        }

        return builder.ToString();
    }
}
