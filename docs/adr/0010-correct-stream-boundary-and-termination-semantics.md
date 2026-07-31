# ADR 0010 — Correct stream boundary and termination semantics

- Status: Accepted, refined by [ADR 0011](0011-per-event-bounds-and-media-type-parsing.md)
- Date: 2026-07-30
- Related: ADR 0008 (Stage 1A transparent request path), ADR 0009 (Stage 1B streaming relay)
- Supersedes: ADR 0009 decision 6 in part, ADR 0009's second known limitation in full

> **Refined in part.** Decision 5's media-type matcher skipped the parameters rather than parsing
> them, and decision 6's relayed content type was still passing through the evidence sanitiser. ADR
> 0011 corrects both, and extends decision 8's "nothing behind the terminator un-ends the response"
> to a per-event bound violation in the same read. Every decision here stands as stated.

## Context

A review of the finished Stage 1A and Stage 1B slices found five defects that a fully green test
suite did not catch, because two of the tests encoded the defective behaviour as the contract.

None of them changed what a client received. All of them changed what AgentSplice said it had
observed, which for this product is the more serious of the two: a gateway that forwards bytes
correctly and records the wrong timeline is a diagnostic tool that produces confident, wrong answers.

The durable invariant these corrections restore:

> Every timestamp and observation must represent a boundary AgentSplice actually observed. Missing
> evidence remains absent. No event may be timestamped before it occurred merely for implementation
> convenience.

## The inaccurate Stage 1B assumptions

ADR 0009 decision 6 stated that `FirstDecodedEvent` and `FirstClientEventFlushed` are stamped with
"the *pre-write* timestamp of the read that completed the first event, because the write that carried
those bytes preceded the decode". The reasoning is half right and the conclusion is wrong. The write
does precede the decode — but a timestamp taken *before* the read that produced the bytes precedes
the arrival of the bytes themselves, so it dates neither operation.

ADR 0009's known limitations stated that reading on after `[DONE]` "costs latency rather than
accuracy". That is also wrong: upstream completion was dated from whatever ended the transport, so a
runtime that held its connection open after finishing stretched the upstream duration and the
generation window derived from it over a stall that produced nothing.

## Decisions

### 1. Four boundaries, four clock readings, four operations

`StreamRelayPump` took one timestamp before awaiting the upstream read and reused it for the first
upstream byte, the first decoded event, the first semantic event, and the first client flush. Four
distinct boundaries collapsed onto one instant, and that instant preceded all four of them.

Each boundary is now stamped at the operation that produced it:

| Boundary | Stamped |
|---|---|
| `FirstUpstreamByte` | immediately after the first `ReadAsync` returns a positive byte count |
| `FirstClientEventFlushed` | at the completion of the write whose bytes completed the first non-comment event |
| `FirstDecodedEvent` | when `SseFrameReader.TryReadFrame` hands out the first complete frame |
| `FirstSemanticEvent` | when the protocol interpreter classifies a frame as carrying output |

The consequence is that time to first byte, flush latency, decode cost, and time to first token are
four measurable quantities rather than one number repeated four times. Under the old scheme every
interval between them was exactly zero, which is indistinguishable from a gateway that is
infinitely fast and from one that is not measuring at all.

Four separate flags replace the single `sawFirstFrame`. One boolean could not express "a keepalive
has been decoded but nothing has been delivered".

### 2. A comment is not a client event

`FirstClientEventFlushed` fires on the first complete **non-comment** SSE event. A comment or
keepalive may set `FirstDecodedEvent`, because a frame was decoded, but a conforming client raises
no event for it, and dating first delivery from a keepalive would report a response as having
reached the client before it carried anything at all.

This matches the existing rule that keepalives are relayed but not counted in `stream.events`.

### 3. Boundaries are appended in the order they occurred, not the order they were learned

The relay writes before it decodes, so the client-flush timestamp is always earlier than the decode
timestamps taken in the same drain — yet it is only *known* to be a client-event boundary once a
non-comment frame turns up. A keepalive and a data event arriving in one read would otherwise append
the decode boundary first and the earlier flush boundary second, leaving the timeline running
backwards.

The drain therefore holds the boundaries it observes and appends them in chronological order:
client flush, then decode, then semantic classification. The timeline stays append-only and
non-decreasing, which every derived duration depends on — the measurement layer drops a negative
interval, so an out-of-order boundary does not produce a wrong number, it makes a whole phase
disappear.

### 4. First-byte evidence on the buffered path is taken in the callback

`BoundedBodyReader` signals the first positive read through a callback. The provider set a boolean
there and read the clock afterwards, so `FirstUpstreamByte` was really "the body finished" or "the
body failed". For a short answer the two are indistinguishable; for a long generation they are the
entire length of the response apart, which is precisely when the boundary is worth having.

The timestamp is now captured inside the callback and assigned once — a later read must not
overwrite the boundary it is not. The same value flows into every branch: success, body too large,
truncated body, and a transport fault after headers.

### 5. Stream media types are matched by the protocol, not by string equality

Both `ChatCompletionStreamRelay` and `ChatCompletionGateway` compared the runtime's content type to
the literal `text/event-stream` for ordinal equality. RFC 9110 makes type and subtype
case-insensitive and permits parameters, so a conforming `text/event-stream; charset=utf-8` was not
recognised as a stream.

`IStreamEventInterpreter` gained `MatchesStreamMediaType(string?)`. The OpenAI implementation takes
the media type token, trims it, and compares it case-insensitively; parameters are ignored rather
than parsed, and a null, empty, or malformed value is simply not a match. It cannot throw on
untrusted header content.

Both call sites now ask the same question of the same implementation, so one response can no longer
be classified two ways.

This defect was latent rather than live: `UpstreamResponseMetadata.Create` already normalised the
media type for evidence, and the comparison happened to be reading that normalised value. It was one
refactor away from being real, and the duplicated literal was the reason it could not be seen.

### 6. The runtime's content type reaches the client unchanged

Fixing decision 5 exposed a genuine transformation. The value forwarded to the client was the
*normalised* media type — lowercased, parameters stripped — not the header the runtime sent. For
SSE that silently dropped a redundant `charset`; for `text/plain; charset=iso-8859-1` it would
change how a client decodes the body, and for any parameterised type it would discard the parameter
the body cannot be parsed without.

`UpstreamResponseMetadata` now carries both: `ContentType` is the bounded token every decision and
trace attribute turns on, and `ContentTypeHeader` is what gets written back, bounded and stripped of
control characters because it reaches a response header verbatim.

Classification and forwarding are separate concerns, and both now read from the value appropriate to
their purpose.

### 7. `FlushResult` is inspected, not discarded

`HttpClientResponseSink.WriteAsync` awaited `BodyWriter.WriteAsync` and returned `Written` unless an
exception was thrown. `PipeWriter` reports a completed or cancelled pipe through its `FlushResult`
without throwing, so a client that stopped consuming could go unnoticed: the relay kept reading the
runtime, kept counting bytes as delivered, and the exchange completed successfully.

`IsCompleted` means the reader is finished with the pipe, so nothing written after it can reach the
client. `IsCanceled` means the flush was cut short and these bytes are not known to have been
delivered. Both now return `ClientGone`, which the relay already handles: it stops reading, disposes
the upstream body, and records `StreamTermination.ClientCancelled` rather than a completed exchange.

### 8. The first valid `[DONE]` is the end of the response

The relay recognised the terminator and kept reading until transport EOF, an idle timeout, a total
timeout, or another terminator. ADR 0009 accepted this in exchange for connection reuse and claimed
it cost latency but not accuracy.

It cost both. A runtime that sends `[DONE]` and holds the connection open made the client wait for
its idle budget, and `UpstreamCompleted` was dated from whatever finally ended the transport — so
the upstream duration, and the generation-throughput window derived from it, both absorbed a stall
during which the runtime produced nothing.

The first complete valid `[DONE]` now ends the relay. Once its bytes have been written and flushed
and the frame recognised:

1. the terminator is marked observed and timestamped;
2. `UpstreamCompleted` is recorded at that instant;
3. no further upstream read is issued;
4. the client response completes normally;
5. the upstream response, body, and timeout budgets are disposed.

A timeout or a reset arriving after the runtime has already said it finished is therefore no longer
expressible, which is the point: the guard clauses that used to reinterpret such a failure as a
successful termination are gone, because the failure cannot occur.

**A second `[DONE]` is not a second completion.** The previous contract required both to be relayed
and the exchange to report three events; that consolidated a runtime anomaly as expected behaviour.
A repeat terminator arriving in a later read is neither read nor forwarded.

#### Same-read post-terminator bytes

The relay forwards bytes before decoding them, so anything a runtime coalesced behind its own
terminator inside one network read has already been written to the client by the time the terminator
is recognised. Those bytes cannot be recalled, and redesigning the proxy into decode-before-forward
to remove them would sacrifice the property that makes bounded flush delay structural rather than
aspirational.

They are treated honestly rather than hidden:

- they are forwarded, and counted in `stream.bytes` and `ClientBytes`, because the client received
  them;
- they are **not** interpreted, so they contribute no events to `stream.events`, no boundaries, and
  no usage;
- `stream.bytes` and `stream.events` can therefore disagree by exactly whatever a runtime emitted
  after declaring itself finished.

Interpreting them would extend a response the protocol had already ended, and would allow a boundary
to be timestamped after upstream completion.

#### The connection-reuse tradeoff

Reading to EOF is what allows a keep-alive connection to be returned to the pool. Disposing a
partially-read response closes that connection instead, so a runtime that lingers after `[DONE]`
costs one connection per exchange.

That is the correct trade. The alternative charges every streamed exchange with the runtime's stall,
in latency the client waits through and in evidence the operator later trusts. A bounded background
drain was considered and rejected for Stage 1B: it would have to be proven not to change any
completion timestamp, not to change generation throughput, not to hold request resources
indefinitely, and not to report the failure of a response that already succeeded — four proofs for a
transport optimisation, in the stage whose subject is measurement correctness.

## Consequences

- `ObservationType` is unchanged. The vocabulary was already right; only the moments it was recorded
  at were wrong.
- `UpstreamResponseMetadata` gained `ContentTypeHeader`. `ContentType` keeps its existing meaning.
- `IStreamEventInterpreter` gained `MatchesStreamMediaType`, and `ChatCompletionStreamRelay` exposes
  it so the orchestrator asks the same question.
- `StreamRelayPump.ForwardAsync` no longer takes a timestamp parameter; it takes clock readings where
  the operations happen.
- Two Stage 1B tests were replaced rather than extended, because they asserted the defective
  behaviour: the boundary-order test expected decode before flush, and the duplicate-terminator test
  required both terminators to be relayed.
- `AgentSplice.UnitTests` now references `AgentSplice.Providers.LmStudio`, for the same reason it
  already references `AgentSplice.Protocols.OpenAI`: the behaviour is deterministic logic that a
  socket cannot be asked to reproduce on demand.

## Tests proving these decisions

| Decision | Test |
|---|---|
| 1 — first byte after the read | `StreamRelayBoundaryTests.The_first_upstream_byte_is_stamped_after_the_read_that_returned_it` |
| 1 — flush completion | `StreamRelayBoundaryTests.The_first_client_event_is_stamped_at_the_flush_that_delivered_it` |
| 1 — four distinct instants | `StreamRelayBoundaryTests.Each_streaming_boundary_carries_its_own_timestamp` |
| 1 — semantic, not first chunk | `StreamRelayBoundaryTests.The_semantic_boundary_belongs_to_the_event_that_carried_output` |
| 2 — keepalive is not delivery | `StreamRelayBoundaryTests.A_keepalive_alone_decodes_an_event_but_delivers_none`, `ChatCompletionStreamingTests.A_stream_of_keepalives_alone_records_no_semantic_boundary` |
| 3 — chronological append | `StreamRelayBoundaryTests.A_keepalive_and_a_data_event_in_one_read_stay_in_chronological_order`, `...A_keepalive_in_an_earlier_read_leaves_the_client_boundary_to_the_data_event` |
| 3 — recorded order | `ChatCompletionStreamingTests.A_streamed_exchange_records_the_streaming_boundaries_in_order` |
| 4 — buffered first byte | `UpstreamFirstByteTests.The_first_byte_is_stamped_when_the_first_chunk_arrived_not_when_the_body_ended` |
| 4 — never overwritten | `UpstreamFirstByteTests.A_later_chunk_never_overwrites_the_first_byte_boundary` |
| 4 — truncated body, absent body | `UpstreamFirstByteTests.A_body_that_ends_early_keeps_the_first_byte_it_did_produce`, `...A_response_that_produced_no_body_records_no_first_byte` |
| 5 — media-type matching | `StreamMediaTypeMatchingTests` (18 cases) |
| 5, 6 — end to end | `ChatCompletionStreamingTests.A_stream_whose_content_type_carries_a_charset_is_still_a_stream` |
| 7 — `FlushResult` | `ClientResponseSinkTests` (5 cases) |
| 7 — relay reaction | `StreamRelayBoundaryTests.A_sink_that_reports_the_client_gone_without_throwing_ends_the_relay` |
| 8 — no read after the terminator | `StreamRelayBoundaryTests.The_protocol_terminator_ends_the_relay_without_another_read` |
| 8 — repeat terminator | `StreamRelayBoundaryTests.A_terminator_in_a_later_read_is_never_consumed`, `ChatCompletionStreamingTests.A_second_terminator_from_a_later_read_is_never_consumed` |
| 8 — stall after the terminator | `ChatCompletionStreamingTests.A_runtime_that_stalls_after_the_terminator_does_not_hold_the_client` |
| 8 — EOF without a terminator | `ChatCompletionStreamingTests.A_stream_that_ends_without_a_terminator_completes_normally`, `StreamRelayBoundaryTests.A_stream_that_ends_without_a_terminator_still_completes_normally` |

## Known limitations

- Bytes a runtime coalesces behind its own terminator inside one network read reach the client and
  are not interpreted, as described above. This is a property of forwarding before decoding, and it
  is preferred to the alternative.
- A runtime that lingers after `[DONE]` costs one upstream connection per exchange, because the
  response is disposed rather than drained.
- `WebApplicationFactory` uses `TestServer`, so `DisableBuffering` remains a no-op in the integration
  suite. That limitation is inherited from ADR 0009 and is unchanged here.
