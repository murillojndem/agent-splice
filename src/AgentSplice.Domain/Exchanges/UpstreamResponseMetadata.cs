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

    private UpstreamResponseMetadata()
    {
    }

    /// <summary>The status the runtime returned.</summary>
    public int StatusCode { get; private init; }

    /// <summary>The media type only, with parameters stripped, or <c>null</c> when none was sent.</summary>
    public string? ContentType { get; private init; }

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
