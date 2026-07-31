using AgentSplice.Domain.Exchanges;
using Xunit;

namespace AgentSplice.UnitTests.Exchanges;

/// <summary>
/// What a runtime's response headers become: a bounded token for evidence, and a header for the wire
/// (ADR 0010, ADR 0011).
/// </summary>
/// <remarks>
/// The two values exist because they are bounded for opposite reasons. Evidence is trimmed so a trace
/// attribute stays small and predictable; a response header is not, because a header cut short is not
/// a shorter header — it is a different one, and a client parsing a truncated <c>boundary</c> gets a
/// body it cannot read.
///
/// Running the relayed value through the evidence sanitiser was the defect: it trimmed, substituted
/// control characters, and truncated at 256, while the documentation called the result verbatim.
/// </remarks>
public sealed class UpstreamResponseMetadataTests
{
    private static readonly DateTimeOffset Observed = new(2026, 7, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_media_type_is_normalised_for_evidence_and_kept_intact_for_the_client()
    {
        var metadata = Create("Text/Event-Stream; charset=utf-8");

        Assert.Equal("text/event-stream", metadata.ContentType);
        Assert.Equal("Text/Event-Stream; charset=utf-8", metadata.RelayableContentType);
    }

    [Fact]
    public void A_long_but_valid_content_type_is_relayed_whole()
    {
        // The evidence bound is 256 characters, and this is well past it. Truncating here would cut
        // the quoted parameter in half and hand the client a header the runtime never sent.
        var contentType = "text/event-stream; charset=utf-8; note=\"" + new string('a', 400) + "\"";

        var metadata = Create(contentType);

        Assert.Equal(contentType, metadata.RelayableContentType);
        Assert.True(contentType.Length > 256);

        // Evidence still gets the short token, so a trace attribute is unaffected by the runtime's
        // choice of parameter length.
        Assert.Equal("text/event-stream", metadata.ContentType);
    }

    [Fact]
    public void A_content_type_beyond_the_relay_bound_is_refused_rather_than_truncated()
    {
        // Refusal leaves the relay to fall back to the normalised token, which says less than the
        // runtime did. Truncation would say something the runtime never did, which is worse.
        var metadata = Create("text/event-stream; note=\"" + new string('a', 2048) + "\"");

        Assert.Null(metadata.RelayableContentType);
        Assert.Equal("text/event-stream", metadata.ContentType);

        // And the response is still an event stream. Relayability answers "may this header be written
        // back"; it says nothing about what the body is, and letting it decide made a conforming
        // stream that was merely too long to forward come out as a buffered response (ADR 0012).
        Assert.Equal("text/event-stream", metadata.ParsedMediaType);
    }

    [Fact]
    public void The_parsed_media_type_requires_the_whole_header_to_conform()
    {
        // The difference between the two tokens. `ContentType` is the text before the first
        // semicolon, kept as evidence of what the runtime appeared to say; `ParsedMediaType` is null
        // unless the whole value parsed, so it cannot be mistaken for proof of validity.
        var metadata = Create("text/event-stream; ===");

        Assert.Equal("text/event-stream", metadata.ContentType);
        Assert.Null(metadata.ParsedMediaType);
    }

    [Fact]
    public void The_parsed_media_type_is_lowercased_like_the_evidence_token()
    {
        Assert.Equal("text/event-stream", Create("Text/Event-Stream; charset=utf-8").ParsedMediaType);
    }

    [Theory]
    [InlineData("text/event-stream\r\nX-Injected: yes")]
    [InlineData("text/event-stream\nX-Injected: yes")]
    [InlineData("text/event-stream\u0000")]
    [InlineData("text/event-stream; charset=\u0007utf-8")]
    public void A_content_type_carrying_a_control_character_is_never_relayed(string contentType)
    {
        // A CR or LF in a header value is a response-splitting attempt. Substituting the character
        // would forward a header the runtime never sent while making it look valid; refusing is the
        // honest answer.
        Assert.Null(Create(contentType).RelayableContentType);
    }

    [Fact]
    public void A_horizontal_tab_is_whitespace_rather_than_a_control_character()
    {
        // RFC 9110 permits HTAB inside a field value as optional whitespace, so refusing it would
        // cost fidelity on a legal header for no security gain.
        const string ContentType = "text/event-stream\t;\tcharset=utf-8";

        Assert.Equal(ContentType, Create(ContentType).RelayableContentType);
    }

    [Fact]
    public void Surrounding_whitespace_is_not_part_of_the_value()
    {
        // RFC 9110 section 5.5 excludes leading and trailing whitespace from a field value, so
        // removing it recovers the value rather than altering it.
        Assert.Equal("text/event-stream", Create("  text/event-stream \t").RelayableContentType);
    }

    [Fact]
    public void A_runtime_that_sent_no_content_type_produces_neither_value()
    {
        var metadata = Create(contentType: null);

        Assert.Null(metadata.ContentType);
        Assert.Null(metadata.RelayableContentType);
    }

    [Fact]
    public void An_upstream_request_id_is_still_bounded_for_evidence()
    {
        // The evidence sanitiser keeps its job. Only the relayed content type was taken out of its
        // hands, because only that value ends up on the wire.
        var metadata = UpstreamResponseMetadata.Create(
            200,
            Observed,
            "application/json",
            new string('r', UpstreamResponseMetadata.MaxRequestIdLength + 64));

        Assert.Equal(UpstreamResponseMetadata.MaxRequestIdLength, metadata.UpstreamRequestId?.Length);
    }

    private static UpstreamResponseMetadata Create(string? contentType) =>
        UpstreamResponseMetadata.Create(200, Observed, contentType);
}
