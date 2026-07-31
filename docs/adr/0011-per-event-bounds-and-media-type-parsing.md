# ADR 0011 — Per-event bounds, terminator precedence, and media-type parsing

- Status: Accepted, refined by [ADR 0012](0012-classification-independent-of-relayability.md)
- Date: 2026-07-30
- Related: ADR 0009 (Stage 1B streaming relay), ADR 0010 (stream boundary and termination semantics)
- Refines: ADR 0010 decision 8 (same-read post-terminator bytes), ADR 0010 decision 6 (relayed content type)

> **Refined in part.** Decision 4 split the content type into an evidence token and a relayable
> header, and protocol classification was then wired to the relayable one — so a conforming event
> stream whose header was merely too long to forward was classified as a buffered response. ADR 0012
> adds the third value that question actually needs. The split itself stands.

## Context

A second review of the Stage 1A/1B slice, taken after the corrections in ADR 0010 landed, found two
framing defects and two inconsistencies between what the code did and what the documentation claimed
it did.

The pattern is the same one ADR 0010 opened with, one layer down. Every one of these passed a green
suite, because a bound that is enforced in the ordinary case and skipped in the interesting one looks
exactly like a bound that works.

## Decisions

### 1. `maxStreamEventBytes` bounds a completed event, not only an unterminated one

`SseFrameReader.Append` appended the arriving bytes, scanned for frame boundaries, and then checked
`PendingBytes` — the bytes after the last *complete* frame — against the ceiling.

Completing a frame resets that count. An event that crossed the ceiling in the same append that
carried its terminating blank line therefore left `PendingBytes` at nearly zero and was accepted, in
full, and handed to the interpreter:

```text
already buffered   1 MiB - 10 bytes     (one event, still open)
arriving chunk     100 bytes + "\n\n"   (closes it)
whole event        > 1 MiB
PendingBytes       ~0
verdict            accepted
```

So the configured ceiling held for every event except the ones that actually reached it. In the
current relay the overshoot is capped by the 16 KiB read buffer, because at most one read's worth can
be added per append — but that is a property of one caller, not of the bound, and `SseFrameReader`
is public.

`Scan` now measures each frame as it completes, using `lastFrameEnd` as its start. An event over the
ceiling is neither enqueued nor stepped over, which leaves its bytes inside `PendingBytes` so the
append that produced them reports the violation. The flag is sticky: a bound that resumed after being
crossed would not be a bound.

Rejected: checking the length in `Append` after the scan. It cannot see individual frame boundaries,
only the last one, so it would have to re-walk the queue — and it would still hand the oversized
frame out to `TryReadFrame` first.

### 2. Events completed before a violation are drained before the bound is enforced

`StreamRelayPump.ForwardAsync` called `reader.Append`, and on `false` aborted immediately — without
draining. Every frame that had completed earlier in that same read was discarded unread.

That contradicts ADR 0010 decision 8. Those frames were written and flushed to the client *before*
`Append` was ever called, so they are evidence regardless of what the rest of the read turned out to
contain. And one of them may be the protocol terminator, in which case the response had already
ended and nothing behind it can un-end it:

```text
read:  data: [DONE]\n\n  <oversized incomplete tail>
was:   InvalidUpstreamStream, connection aborted
now:   ProtocolTerminatorReceived, exchange completed
```

The order is now append, drain, then enforce:

```csharp
var withinBounds = reader.Append(chunk.Span);

DrainFrames();

if (sawTerminator || withinBounds)
{
    return null;
}

client.Abort();
return Finish(StreamTermination.LimitExceeded, ...);
```

The bound still wins when the violation comes *first*: an oversized event ahead of the terminator is
never enqueued (decision 1), so the terminator behind it is never reached and the stream is abandoned
as it should be. There is no completion to protect in that case, and a client must not be handed a
stream that stops early and closes as though it were whole.

This is reachable in practice whenever `maxStreamEventBytes` is configured below the 16 KiB read
size, which the test suite itself does.

### 3. Media-type classification parses the parameters instead of skipping them

ADR 0010 decision 5 replaced whole-string equality with "take everything before the first semicolon
and compare case-insensitively". That fixed `text/event-stream; charset=utf-8` and introduced the
mirror-image error: `text/event-stream; ===` and `text/event-stream; invalid parameter` were also
accepted. A classifier that says "this is an event stream" about a header it never read is making the
same unchecked claim in the other direction.

`OpenAiMediaTypes` now validates the whole value against the RFC 9110 grammar — `type "/" subtype`
as two tokens with nothing between them, then `*( OWS ";" OWS [ parameter ] )` with each parameter a
token, an equals sign, and either a token or a quoted string. Semicolons inside a quoted value do not
split a parameter.

**`text/event-stream;` is accepted, deliberately.** RFC 9110 section 5.6.6 writes the parameter itself
as optional inside the repetition — `*( OWS ";" OWS [ parameter ] )` — so an empty or trailing
semicolon is conforming. The review that prompted this ADR listed it as invalid; refusing it would
reject a sloppy but legal sender, which is the exact class of failure this matcher exists to stop.

Rejected: `System.Net.Http.Headers.MediaTypeHeaderValue.TryParse`. An architecture test forbids
`AgentSplice.Protocols.OpenAI` from referencing `System.Net.Http`, and that rule is load-bearing —
it is what keeps transport types out of the protocol modules. Rejected: a `FrameworkReference` on
`Microsoft.AspNetCore.App` to reach `Microsoft.Net.Http.Headers`, which pulls a web framework into a
protocol library for one parser. The grammar is small enough to implement directly, as SSE framing
and the request scanner already are, and it allocates nothing and throws on nothing.

### 4. The relayed content type is validated, never repaired

ADR 0010 decision 6 separated the media-type token kept for evidence from the header written back to
the client. The separation was right and the implementation was not: the relayed value still went
through `Bound`, the evidence sanitiser, which trims, substitutes control characters, and **truncates
at 256 characters**.

A header cut at 256 characters is not a shorter header. It can end inside a quoted parameter or halve
a multipart `boundary`, and the client then cannot parse a body the runtime sent correctly. The
documentation called the result verbatim; it was not.

The two values are now bounded for their own reasons and by their own rules:

| | `ContentType` | `RelayableContentType` |
|---|---|---|
| Purpose | classification, evidence, trace attributes | the response header |
| Bound | 128 characters | 1024 characters |
| Over the bound | truncated | **refused**, leaving `null` |
| Control characters | substituted | **refused**, leaving `null` (HTAB excepted — RFC 9110 permits it as whitespace) |
| Case | lowercased | as sent |
| Parameters | stripped | kept |

Refusal rather than repair, because the alternatives are worse in different ways: truncating produces
a header the runtime never sent while leaving it looking valid, and a `CR` or `LF` in a header value
is a response-splitting attempt that must not be laundered into something plausible. When the header
is refused the relay falls back to the normalised token, which says less than the runtime did rather
than something untrue.

`RelayableContentType` must never reach a log, a span attribute, or `SafeDetails`. Its only
destination is the wire; `ContentType` is what evidence records. The name says so and so does the
member's documentation.

### 5. `IsCommentOnly` was renamed to `DispatchesClientEvent`

The property returned `DataLineCount == 0`, which is true for a comment and equally true for a bare
`id`, a `retry` directive, and an `event` name with no payload. The logic was right — the SSE grammar
dispatches nothing when the data buffer is empty — and the name was not, which invited the delivered-
event count and the first-client-event boundary to be read as covering keepalives alone.

Named for dispatch, in the positive, because that is the question both call sites actually ask. Note
the edge it gets right either way: `data:` with an empty value leaves a line feed in the data buffer
rather than nothing, so it **does** dispatch an event, and keying on "carries no `data` field" rather
than "carries no bytes" is what makes that work.

## Consequences

- `SseFrameReader.Append` keeps its `bool` return. A richer result type was considered and rejected:
  the pump needs "may I continue", and the frames it should still drain are already reachable through
  `TryReadFrame`.
- `UpstreamResponseMetadata.ContentTypeHeader` became `RelayableContentType`, and
  `MaxContentTypeHeaderLength` became `MaxRelayableContentTypeLength` (256 → 1024).
- `SseFrame.IsCommentOnly` became `SseFrame.DispatchesClientEvent`, with inverted sense.
- The relay and the orchestrator fall back to `ContentType` when `RelayableContentType` is refused,
  rather than straight to the protocol default.
- FR-STR-008 and FR-STR-014 gained the precision these decisions turn on.

## Tests proving these decisions

| Decision | Test |
|---|---|
| 1 — completed event over the bound | `SseFrameReaderTests.An_event_that_crosses_the_bound_in_the_append_that_ends_it_is_still_refused` |
| 1 — unterminated event over the bound | `SseFrameReaderTests.An_event_that_crosses_the_bound_before_it_ends_is_refused` |
| 1 — exactly at the bound | `SseFrameReaderTests.An_event_exactly_at_the_bound_is_accepted` |
| 1 — many small events exceeding it in total | `SseFrameReaderTests.Many_small_events_totalling_more_than_the_bound_are_accepted` |
| 1 — split across reads | `SseFrameReaderTests.A_large_event_split_across_many_reads_is_refused_at_the_read_that_crosses_the_bound` |
| 1 — sticky refusal | `SseFrameReaderTests.A_reader_that_refused_once_refuses_everything_after` |
| 1 — end to end | `ChatCompletionStreamFailureTests.A_complete_event_larger_than_its_bound_is_stopped_too` |
| 2 — frames before a violation survive | `SseFrameReaderTests.Events_completed_before_a_violation_are_still_readable` |
| 2 — terminator wins | `StreamRelayBoundaryTests.A_terminator_survives_an_oversized_event_behind_it_in_the_same_read` |
| 2 — bound wins when it comes first | `StreamRelayBoundaryTests.An_oversized_event_ahead_of_the_terminator_still_ends_the_stream` |
| 2 — end to end | `ChatCompletionStreamFailureTests.An_oversized_event_behind_the_terminator_does_not_undo_the_completion` |
| 3 — conforming values | `StreamMediaTypeMatchingTests.A_conforming_event_stream_content_type_is_recognised` |
| 3 — the empty-parameter production | `StreamMediaTypeMatchingTests.An_empty_parameter_is_permitted_by_the_grammar` |
| 3 — malformed parameters and media types | `StreamMediaTypeMatchingTests.A_malformed_content_type_is_not_an_event_stream` (15 cases) |
| 4 — long header relayed whole | `UpstreamResponseMetadataTests.A_long_but_valid_content_type_is_relayed_whole`, `ChatCompletionStreamingTests.A_long_content_type_reaches_the_client_whole` |
| 4 — refusal rather than truncation | `UpstreamResponseMetadataTests.A_content_type_beyond_the_relay_bound_is_refused_rather_than_truncated` |
| 4 — header injection refused | `UpstreamResponseMetadataTests.A_content_type_carrying_a_control_character_is_never_relayed` |
| 4 — HTAB is whitespace | `UpstreamResponseMetadataTests.A_horizontal_tab_is_whitespace_rather_than_a_control_character` |
| 5 — dispatch, not comments | `SseFrameReaderTests.An_id_or_retry_field_frames_an_event_a_client_dispatches_nothing_for`, `...An_empty_data_value_still_dispatches_an_event` |

Every one of them was run against a deliberately reintroduced copy of the defect it covers: 22 failed
and the rest held.

## Known limitations

- Unchanged from ADR 0010: bytes a runtime coalesces behind its own terminator inside one read reach
  the client and are not interpreted, so `stream.bytes` and `stream.events` can disagree by that
  amount. Decision 2 extends the same reasoning to a bound violation behind the terminator.
- A runtime whose `Content-Type` exceeds 1024 characters or carries a control character has its
  header replaced by the normalised media type. That is a deliberate narrowing rather than a
  transformation, and the exchange still records what was observed.
