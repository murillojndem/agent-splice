# ADR 0025 — Stage 1D: a stream's status is committed with its first byte, and an error code only when an envelope reached the client

- Status: Accepted
- Date: 2026-10-18
- Related: [ADR 0008](0008-stage-1a-transparent-request-path.md) (decision 12: `completed` means the
  transport cycle finished), [ADR 0010](0010-correct-stream-boundary-and-termination-semantics.md)
  (decision 1: a boundary is stamped at the operation it names),
  [ADR 0023](0023-stage-1d-the-first-dated-trace-report.md) (the trace report whose findings 2 and 3
  this closes)
- Supersedes: the definition of `StreamedResponse` and the commit point in decision 4 of
  [ADR 0009](0009-stage-1b-streaming-relay.md); closes the gap
  [ADR 0019](0019-stage-1d-sample-client-configurations-and-a-quickstart.md) recorded as found and not
  fixed

## Context

The relay committed a stream's status the moment the runtime's headers arrived: it set the status and
the correlation headers and called `StartAsync`. Kestrel writes a started response's headers into its
output pipe and sends them only with the first body write, so the status was unchangeable and still
unsent. LM Studio sends a stream's headers at once and its first byte only after it has processed the
prompt. A timeout in that window, or a runtime that dropped the connection then, made the relay abort
the response, and the client received nothing at all: no status line, no `x-agentsplice-request-id`, a
reset. ADR 0019 found it by hand; the first trace report met it in a real session, where OpenCode logged
a closed socket and retried (its finding 2). The in-process test host sends headers as soon as they are
committed, so no test could see it.

The same exchange recorded `errorCode: agentsplice_upstream_timeout`, which the API defines as the code
the client received (finding 3). Every abandoned response did the same, and so did the catch-all for a
fault after a response started, which also tried to write a 500 over a started response and threw
"headers are read-only". A streaming request answered with an ordinary body that failed mid-body ended
with a clean chunked close, handing the client truncated JSON that looked whole. The buffered path
recorded the client's completion, and handed the record to the store, before writing a byte, so a
client that left during the write was recorded as completed with every byte "forwarded". And only a
mid-stream timeout ever reached the timeline: a 504 on the buffered path, where LM Studio sends its
headers only with the finished answer, threw its phase away, and a refused connection or a truncated body recorded nothing.

The readiness review held before the alpha reproduced each of these on real Kestrel and asked for one
slice to close them, with a test on the server the gateway ships on.

## Decisions

### 1. A relayed response is committed with the runtime's first byte

The relay prepares the client's status, media type, and headers when the runtime's headers arrive, and
commits them just before it writes the runtime's first body byte, or at the end of a response that had
none. On Kestrel that is what reached the wire anyway — headers left with the first write — so a stream
that succeeds is unchanged byte for byte and nearly unchanged in time.

### 2. A failure before that byte is answered with the gateway's error

Until the first byte nothing is committed, so a failure in that window is still the gateway's to
answer, exactly as before the runtime's headers: `504` `agentsplice_upstream_timeout` for an idle or
total budget, `502` `agentsplice_invalid_upstream_response` for a lost connection, with every
correlation header, and the runtime's request aborted. This is the gateway's own error for a transport
failure, which the API rules require, extended by one phase; no relayed byte changes.

`StreamedResponse` no longer means "committed to the client". It now means the runtime answered with an
event stream and the relay began, so such an exchange still records how the runtime's stream ended —
`timeout` or `connection_lost` — and a client that leaves before the first byte still records
`client_cancelled`, as the trace report's cancelled exchanges did. Moving `BeginStreaming` to the commit
instead would have recorded `not_applicable` for both, against the same record's `upstream.streamed:
true`.

> **Refined by ADR 0028.** The budget that elapses before the first byte is now the first-byte one,
> `timeouts:firstByte`; the idle budget applies only after that byte. A timeout in this window is
> answered with the same `504` and recorded as `timeout.phase: FirstByte`.

### 3. The commit is a boundary, stamped when the headers were flushed

`IClientResponseSink.StartAsync` now flushes the headers before it returns, and the new
`client_response_started` boundary is stamped when it has. Stamping it at the commit alone would date a
moment the client had not been sent anything, which ADR 0010 forbids. Every response the gateway writes
records it, so the timeline always says when the client was sent its status — the moment ADR 0019 found
invisible.

### 4. The gateway writes every response, then records it

`ChatCompletionGateway` writes the relayed answer and its own error envelopes itself, through the sink,
and records the exchange only after the write returned; the endpoint adds nothing, and
`ChatCompletionOutcome` is gone. Every exchange now ends with exactly one client-side boundary:

- `client_completed`, with `client.response` `relayed` or `gateway_error`, when the response was written
  in full;
- `client_cancelled`, when the client left — including during a buffered write, which is no longer
  recorded as completed;
- `client_response_abandoned`, when AgentSplice abandoned a response it had started.

The exchange's completion time is the same instant, and `exchange.total.duration` runs to that boundary,
so a failed or cancelled exchange has a total too, and it agrees with the duration the metrics export.

### 5. An error code is recorded only for an envelope that was written

`ExchangeRecord.Error`, stored as `errorCode`, is the envelope the client received. A response abandoned
after it started records its failure class and termination and no code; the span and the metrics keep
the failure's error type, because the response failed whether or not an envelope said so. With decision
2, a failure before the first byte does send its envelope, so its code is now true.

### 6. A started response is abandoned on failure, event stream or not

A streaming request answered with an ordinary body that fails after its first byte is now abandoned like
a stream. A truncated JSON body closed politely is as much a false success as a truncated stream.

### 7. A fault after the response started abandons it

The catch-all aborts a started response and records the fault as `internal_error` with no code, rather
than writing an envelope over it. The recorder now computes a transition before it marks the exchange
terminal, so a transition the domain refuses no longer leaves a row that is neither terminal nor
honest; the catch-all can still end it.

### 8. Every upstream failure is on the timeline

A timeout is recorded as `timeout_fired` with its `timeout.phase` wherever it elapsed: before a
stream's headers, while a buffered body was read, or mid-stream. Any other failure is recorded as the
new `upstream_failed`, with `upstream.failure` — the classification, such as `Unreachable` or
`InvalidResponse` — and the provider's bounded detail, such as `upstream.stream: connection.lost`,
`upstream.completion: body.truncated`, or `upstream.socket_error: ConnectionRefused`. Never a message or
a host (FR-CHAT-008, FR-TRACE-005).

### 9. A client that leaves during model discovery cancelled

A client that disconnects while its model is being discovered was recorded as
`agentsplice_runtime_unavailable`, with a 502 written to nobody. The lookup it abandoned says nothing
about the runtime, and it is now a cancellation. The discovery cache already ignored such a lookup.

### 10. A record keeps the trace id it was given

A request refused before its model was known was given an `x-agentsplice-trace-id` and stored with
none, because the trace id was read from an exchange it does not have. The record carries its own now.

### 11. `stream.client.bytes` counts what was handed to the client's connection

It is recorded by whatever wrote the bytes and only when it did: absent when the client received an
error envelope in place of the runtime's answer, and absent for a buffered answer whose write the
server reported unfinished because the client had gone, where the runtime's body length used to be
reported as delivered. The count is taken when each write returned, which is when the server accepted
the bytes — for a buffered answer that can be all of it before the client reads any, so on that path it
says what was handed to the connection, not what was read; an abandoned response ends in a
reset, which can discard the last of them before they arrive, so for an abandoned response it is an
upper bound.

## Consequences

- A stream that fails before its first byte now reaches the client as the documented `504` or `502`
  envelope with its correlation headers. OpenAI-compatible SDKs treat both as retryable, as they did
  the reset, and the client now has a code and an exchange identifier. After the first byte nothing
  changes for the client.
- In the in-process test host, a stream's headers now arrive with its first byte rather than with the
  runtime's headers, as they always did on Kestrel.
- The timeline vocabulary gains `client_response_started`, `client_response_abandoned`, and
  `upstream_failed`; `client_completed` gains its `client.response` detail and now ends failures whose
  envelope was written. Records stored before this change keep what they recorded: an exchange such as
  the trace report's `d8f5a743` names a code that was not delivered and has no client boundary, which
  is how a record from before this decision can be told apart.
- No configuration key, error code, error type, or metric name changes. `agentsplice.stream.events` and
  `agentsplice.stream.bytes` record no data point for a stream answered with an envelope, rather than a
  zero.

## Tests proving these decisions

Each was run against its reintroduced defect and failed; the changelog lists them.

- `KestrelWireTests`, on a real Kestrel listener on the IPv4 loopback through
  `WebApplicationFactory.UseKestrel`, new in .NET 10: a stream that stalls before its first byte gets a
  `504` with its correlation headers, and one whose runtime drops gets a `502`, where the code before
  this decision left a reset; a successful stream is byte for byte; a mid-stream stall is still
  abandoned and records no code. A fifth, in which a raw socket read the status line of a 32 MiB
  buffered answer and hung up, was removed after CI: Kestrel takes such an answer into its own buffers
  within milliseconds on Windows and on GitHub's Linux runners, so the write finished first unless the
  client's reset happened to beat the copy, and the test proved nothing either way.
- `ChatCompletionCancellationTests`: the gateway, composed as the host composes it, writing a buffered
  answer to a client that took its status and was gone at its body, is recorded as cancelled, with no
  `client_completed` and no bytes claimed delivered.
- `ChatCompletionStreamFailureTests`: the same before-the-first-byte cases in process, with the record —
  the failure class, the stream termination, the phase or the lost connection, the code, and
  `client_completed` with `gateway_error`; abandoned streams record no code and
  `client_response_abandoned`; a fault after the response started is abandoned with no code; and an
  ordinary body cut off mid-answer does not end cleanly.
- `FailureEvidenceTests`: on the buffered path, the response-header and total phases, a refused
  connection's socket error, a truncated and an oversized body, a rejected credential, a relayed answer
  that records no failure, a request refused before its model was known with its trace id, and a client
  that leaves during discovery recorded as cancelled.
- `StreamRelayBoundaryTests`: the response starts at the first read that returned bytes, after which
  the boundary is stamped; a timeout or a reset before it leaves the response unstarted and unaborted;
  an empty answer is committed; a client gone at the commit is a cancellation; a failure after the
  first byte, stream or ordinary body, is abandoned.
- `ExchangeRecorderTests`: a refused transition leaves the exchange open; an abandoned response records
  no code; a written envelope records its code and `gateway_error`; every ending has a total; no bytes
  are claimed for an answer that was not delivered; a timeout records its phase and another failure its
  classification, and a cancellation neither.

## Alternatives considered

- **Flush the headers at the runtime's headers** (ADR 0019's first option). One line, and the client
  would learn its exchange identifier at once. But the status would then be committed and sent, and a
  later failure would still be a reset with no stable error, only mid-body instead; the code recorded
  would stay undelivered; and the AI SDK, which retries a failed call silently, would see the failure
  while reading the body, which it does not retry.
- **Commit at the first byte, but flush after a bounded hold**, so a client with its own header timeout
  is not starved through a long prompt. It adds a timer racing the read, and is worth it only if clients
  are seen timing out on headers.
- **An SSE comment at the commit, or as a keepalive.** Valid SSE that conforming parsers ignore, but
  bytes the runtime never sent, against the byte-for-byte rule; adapter territory.
- **A gateway-authored SSE error event before aborting.** ADR 0009 decision 5 rejected it as
  indistinguishable from something the runtime said.
- **Move `BeginStreaming` to the commit.** Rejected in decision 2.
- **Redefine `errorCode` as the code the gateway classified,** or add a second field for the delivered
  one. The failure class already says why; one field meaning delivery is the unambiguous one.
