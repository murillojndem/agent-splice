# ADR 0012 — Protocol classification is independent of relayability

- Status: Accepted
- Date: 2026-07-30
- Related: ADR 0010 (stream boundary and termination semantics), ADR 0011 (per-event bounds and media-type parsing)
- Refines: ADR 0011 decision 4

## Context

ADR 0011 split the runtime's `Content-Type` into a bounded token kept as evidence and a validated
header written back to the client. That split was right. Wiring protocol classification to the second
of those was not.

`ChatCompletionStreamRelay` and `ChatCompletionGateway.IsStream` both asked
`MatchesStreamMediaType(metadata.RelayableContentType)`. `RelayableContentType` is `null` whenever the
header may not be written back — over 1024 characters, or carrying a control character.

For the control-character case that happens to give the right answer, because a control character
makes the value invalid under RFC 9110 anyway. For the length case it does not:

```text
Content-Type: text/event-stream; note="<1100 characters>"

RelayableContentType   null      (too long to forward)
ContentType            "text/event-stream"
streamed               false     ← wrong
```

The result was internally contradictory. The client received `Content-Type: text/event-stream` from
the fallback and read an event stream, while the gateway treated the response as buffered: no SSE
framing, no `[DONE]` handling, so the relay waited for EOF or an idle timeout, recorded no decoded or
semantic boundaries, applied none of the streaming policy, and wrote `upstream.streamed = false` into
the evidence.

This is the same failure mode ADR 0010 and ADR 0011 were written about, arriving through a new door:
a question answered from a value that was never about that question.

> Relayability answers whether the whole header may be written to the client. It does not answer
> whether the body is an event stream.

## Decisions

### 1. Metadata carries three values, one per question

| Property | Question it answers | Bound |
|---|---|---|
| `ContentType` | what did the runtime appear to say? | 128, truncated, lowercased, parameters stripped |
| `ParsedMediaType` | what media type did a **well-formed** header name? | none needed — it is a short token or `null` |
| `RelayableContentType` | may this header be written back verbatim? | 1024, refused rather than truncated |

`ParsedMediaType` is the value protocol classification asks about, and the only one that may be used
for it.

`ContentType` keeps its existing meaning deliberately. It is produced by taking the text before the
first semicolon, so it survives a malformed header and records what the runtime appeared to say —
which is exactly when an operator most wants to see it. Its documentation now says plainly that it is
not proof of validity and must not be classified from; making it `null` for a malformed header would
have traded one silent failure for a loss of evidence.

### 2. The parse happens once, before any bound can discard the value

`ParsedMediaType` is computed in `UpstreamResponseMetadata.Create`, from the header as received,
before either of the other two values has trimmed, truncated, or refused anything. Deriving it later
from `ContentType` would reintroduce exactly the hole ADR 0011 closed: `text/event-stream; ===`
yields the token `text/event-stream` under a `Split(';')`, and the whole point of the parser is that
a caller cannot mistake the text before the first semicolon for proof that the header was valid.

### 3. The RFC 9110 media-type grammar moved to the domain

The parser was in `AgentSplice.Protocols.OpenAI`, which cannot be reached from `AgentSplice.Domain`.
Rather than duplicate it or retain the whole header just so the protocol module could re-parse it
later, the grammar itself moved to `AgentSplice.Domain.Exchanges.MediaTypeGrammar`.

The split follows the two questions. *"Is this well-formed, and what media type does it name?"* is
HTTP, and belongs beside the type that already normalises and sanitises response headers.
*"Is that media type mine?"* is the protocol's, and `OpenAiMediaTypes.IsEventStream` is now one line:

```csharp
public static bool IsEventStream(string? contentType) =>
    string.Equals(MediaTypeGrammar.Parse(contentType), EventStream, StringComparison.Ordinal);
```

`IStreamEventInterpreter.MatchesStreamMediaType(string?)` keeps its signature and its full validation,
so the port stays correct for any input, and the 41 matcher tests carry over unchanged.

Rejected: a parser in each module. Two implementations of one grammar is how they drift apart, and
this whole sequence of ADRs is about values that stopped meaning what their name said.

Rejected: `System.Net.Http.Headers.MediaTypeHeaderValue`, again. An architecture test forbids
`System.Net.Http` in the domain and in the protocol modules, and that rule is load-bearing.

## Consequences

- `UpstreamResponseMetadata` gained `ParsedMediaType`. Both classification call sites use it; nothing
  else does.
- `AgentSplice.Domain.Exchanges.MediaTypeGrammar` is new and public. It is pure grammar — no
  allocation beyond the token it returns, and it throws on nothing, because a runtime-supplied header
  is untrusted text.
- `OpenAiMediaTypes` shrank to the media types themselves plus one comparison.
- A conforming event stream whose header is too long to forward is now served as a stream, with the
  client told `text/event-stream` rather than the whole header. Narrower than the runtime's answer,
  and never wrong.

## Tests proving these decisions

| Decision | Test |
|---|---|
| 1 — length does not decide streaming | `UpstreamResponseMetadataTests.A_content_type_beyond_the_relay_bound_is_refused_rather_than_truncated` |
| 1 — end to end | `ChatCompletionStreamingTests.A_content_type_too_long_to_forward_is_still_an_event_stream` |
| 2 — the whole header must conform | `UpstreamResponseMetadataTests.The_parsed_media_type_requires_the_whole_header_to_conform` |
| 2 — normalisation | `UpstreamResponseMetadataTests.The_parsed_media_type_is_lowercased_like_the_evidence_token` |
| 3 — the grammar is unchanged by the move | `StreamMediaTypeMatchingTests` (41 cases, carried over) |

Both new tests were run against a deliberately reintroduced copy of the defect and failed; the
end-to-end one failed by waiting out its read budget, which is the buffered path taking a stream.

## Known limitations

Two smaller points were raised alongside this defect and are **deliberately not addressed here**,
because they change no behaviour a client or an operator can observe:

- `obs-text` is implemented as `>= 0x80` rather than `%x80-FF`, so a parameter value containing, say,
  `U+0100` is accepted where the grammar would not have it. Tightening it is one line, and it is not
  obviously an improvement: the header arrives as a decoded .NET string, so a UTF-8 `charset` comment
  in a non-Latin-1 script would then be rejected and the response misclassified — the failure this
  sequence of ADRs exists to prevent. It deserves a decision rather than a reflex.
- "Verbatim" overstates what is relayed. The value comes from
  `response.Content.Headers.ContentType?.ToString()`, which is `HttpClient`'s reparse of the header
  rather than the original bytes; it preserves the media type and its parameters but not necessarily
  their exact spacing. The accurate claim is that AgentSplice removes and rewrites nothing, which is
  what the documentation should say. Likewise `Relayable` uses `string.Trim()` where RFC 9110 defines
  OWS as space and horizontal tab only.
