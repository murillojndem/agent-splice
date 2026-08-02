# ADR 0009 — Stage 1B streaming relay and timeline

- Status: Accepted, partly superseded by [ADR 0010](0010-correct-stream-boundary-and-termination-semantics.md) and, for decision 12, by [ADR 0013](0013-stage-1c-metadata-store.md)
- Date: 2026-07-30
- Related: ADR 0006 (durable core), ADR 0007 (Stage 0 toolchain), ADR 0008 (Stage 1A transparent request path)

> **Superseded in part.** Decision 6's rule for stamping `FirstDecodedEvent` and
> `FirstClientEventFlushed`, and the second entry under "Known limitations", were reviewed and found
> to be wrong. ADR 0010 replaces both and explains why. Every other decision here stands. This
> document is kept as written: the reasoning that produced the error is part of the record.

## Context

Stage 1A refused `stream: true`. Every real agent client streams, so AgentSplice could not observe
the exchanges its users actually run — and evidence about that boundary is the whole product.

Stage 1B accepts streaming requests and, more importantly, makes the resulting timeline true: first
upstream byte, first decoded event, first semantic output event, and first client flush recorded
separately, with every way a stream can end classified rather than collapsed into "it stopped".

Much of the vocabulary already existed and was unreachable — nine `StreamTermination` members, four
`ObservationType` boundaries, `TimeoutPhase.IdleStream`, `CompletionExchange.BeginStreaming`,
`FailureClass.InvalidUpstreamStream`. This stage lights them up rather than inventing them.

Decisions 11 and 14 came out of reviewing the finished slice against these documents rather than out
of building it, which is why they read as corrections.

## Decisions

### 1. Byte-transparent relay, not decode-and-re-emit

Upstream bytes are written to the client and flushed before anything decodes them. An incremental
frame reader observes the same bytes purely for evidence and has no authority over the relay.

Rejected: decoding each event, modelling it, and re-emitting it. It is the shape `docs/ARCHITECTURE.md`
sketched, and it is wrong for the same reason ADR 0008 decision 1 rejected re-emitting a request
document — a writer normalises what a runtime wrote. It would also put a parse between an upstream
byte arriving and it reaching the client, making bounded flush delay a promise about decoder speed
rather than a property of the structure.

The consequence is that valid SSE, split events, split UTF-8 sequences, CRLF, multi-line `data`,
comments, and keepalives are all free rather than earned: a conforming client buffers until the blank
line, and chunk boundaries were never event boundaries.

### 2. Framing and meaning are separate layers

`SseFrameReader` (Application) knows where an event begins and ends and nothing else. An
`IStreamEventInterpreter` implemented by the protocol module knows that `[DONE]` ends an OpenAI
stream, that a role-announcing first chunk is not output, and where usage lives.

Rejected: one class that does both. SSE is a transport grammar shared by more than one protocol, and
fusing the two would make the next protocol a rewrite rather than an implementation. This is the
structural form of FR-STR-006 and FR-STR-009.

### 3. The provider returns a classified byte source, not a `Stream`

`IUpstreamResponseBody.ReadAsync` returns bytes, a clean end, or an `UpstreamFailure`.

Rejected: returning `System.IO.Stream`. It would satisfy the module boundaries, but the difference
between a client disconnect, an idle stall, and an expired total budget is *which* cancellation token
fired, and those tokens exist only inside the provider. Attribution would have had to move to a layer
that cannot see the evidence for it.

The caller owns the read buffer, so relaying a stream of any length allocates nothing per read. The
returned body owns the response, the connection, and all three budgets, because their lifetime is the
stream's rather than the call's.

### 4. `StreamedResponse`, not `Streaming`, governs termination

`Streaming` records what the client asked for. `BeginStreaming()` is called when a `text/event-stream`
response is committed to the client, which is also the moment the status becomes unchangeable, and
`StreamedResponse` records that.

The invariant "a streamed exchange must state how its stream ended" is keyed on the second. Keyed on
the first, a `stream: true` request answered with a buffered JSON error would have been forced to
invent a termination for a stream that never existed. `Accept` now starts at `NotApplicable` rather
than `Unknown` for the same reason: at acceptance there is no stream whose ending is unknown.

### 5. Malformed payloads are observed; bounds abort

A `data:` value that is not valid protocol JSON is recorded as `MalformedEvent`, relayed verbatim,
and completes the exchange with no failure class. The client's own parser is the authority on the
runtime's protocol — the same reasoning by which ADR 0008 decision 12 relays a runtime's
`429 text/plain` rather than substituting a gateway error.

A single event exceeding `limits:maxStreamEventBytes`, or a connection lost mid-stream, abandons the
client connection instead. Once the status is committed, an event stream that stops early but closes
cleanly is indistinguishable from a complete one for any client that does not require a terminator,
so `Abort()` is the only remaining way to say "this is not a whole answer".

Rejected: injecting a synthetic terminal SSE error event. It is a semantic transformation, and worse,
it is indistinguishable to the client from something the runtime said. A truncated stream is honestly
ambiguous; a synthetic event is confidently wrong.

`StreamTermination.LimitExceeded` was added rather than reusing `MalformedEvent`. One describes the
runtime's behaviour and the other AgentSplice's own policy, and reporting a gateway decision as
runtime misbehaviour is exactly the misattribution this product exists to remove.

### 6. Timeline boundaries are stamped where they were observed

`ExchangeRecorder.Observe` gained an explicit-timestamp overload, and the request path now stamps
acceptance and body-read from the transport's clock, response headers from
`UpstreamResponseMetadata.HeadersReceivedAt`, and first byte from the read that returned it.

This fixes a live Stage 1A defect: every boundary was stamped when control returned, so
`agentsplice.time_to_headers` measured time until the whole body had been read. The metric was
plausible, published, and wrong — which is worse than absent.

~~`FirstDecodedEvent` and `FirstClientEventFlushed` are stamped with the *pre-write* timestamp of the
read that completed the first event, because the write that carried those bytes preceded the
decode.~~ **Superseded by ADR 0010 decision 1.** The write does precede the decode, but a timestamp
taken before the read that produced the bytes precedes the arrival of the bytes themselves, so it
dates neither operation — and reusing it for four boundaries made four latencies indistinguishable
from zero. Each boundary is now stamped at the operation that produced it.

`FirstSemanticEvent` fires only on the first event carrying output: an OpenAI-compatible stream's
first chunk usually announces a role, and counting it would make time to first token measure time to
first chunk.

### 7. Generation throughput is derived; prompt throughput is not

Generation throughput is measured over the observed decode window — first semantic event to upstream
completion — carrying the provenance of the token count it was derived from. The window excludes the
first token's own decode latency while counting that token, a bias that is negligible over a long
generation and material over a very short one. It is documented in `docs/OBSERVABILITY.md` rather
than corrected, because dividing by one fewer token would invent a number the runtime never reported.

Prompt throughput is **absent by design, not deferred**. Nothing AgentSplice can observe marks the
end of prompt processing, so the only available interval is time to first token, which measures the
prompt, the queue, and the network together. Publishing that under a prompt-throughput name is the
conflation FR-OBS-005 exists to prevent. It becomes derivable only with runtime-log evidence.

### 8. The idle budget is re-armed, and the header budget is disarmed

Three linked cancellation sources are created once per stream. The idle budget is re-armed before
each read with `CancelAfter`, which reschedules an existing timer, so a stream of any length costs one
timer and no allocations on the read path.

The response-header budget is disarmed the moment headers arrive. Left armed, its token is signalled
during every stream that outlives it, and a classifier consulting it would report each mid-stream
stall as a runtime that was slow to answer — pointing an operator at prompt processing when the
problem is generation. The classifier's order is now client, total, idle, headers, connect.

### 9. Refuse rather than queue at the concurrency limit

`limits:maxConcurrentCompletions` (default 64) applies to `POST /v1/chat/completions` alone and
rejects with `429`, `rate_limit_error`, `agentsplice_gateway_overloaded`, and `Retry-After`.

Rejected: a queue. It converts an overload into unbounded latency, and an agent loop cannot tell a
slow model from a full gateway or back off from either. Rejected: `503`. A `429` is what every OpenAI
SDK already retries on, and it is honest — AgentSplice is limiting the rate.

The limit covers completions only, so discovery and later health endpoints stay answerable exactly
when the gateway is saturated, which is when an operator most needs to ask what it is doing.

`maxStreamEventBytes` is deliberately not validated against `maxUpstreamCompletionBodyBytes`. The two
bound different things — a whole buffered response and a single frame under assembly — and coupling
them would make an operator who tightens one have to tighten the other for no reason the memory
arithmetic supports.

### 10. Kestrel's minimum response data rate is disabled

A local model can legitimately produce one token every few seconds, below Kestrel's default of
240 bytes/s. Left at the default, Kestrel aborts the response mid-stream and AgentSplice would record
that as a client disconnect — blaming the client for a limit the gateway imposed on itself. The
runtime's own `timeouts:idleStream` is the bound that belongs there, and it is configurable per
runtime. No test catches this; it is a one-line omission that produces a misattributed termination in
the field.

### 11. Upstream connection time is measured, by taking over connection establishment

`docs/OBSERVABILITY.md` requires connection time to be distinguished from every other latency phase,
unconditionally. It was initially removed as underivable — connection establishment happens inside
`SocketsHttpHandler`, below anything the request path can see — but that was a limit of the
implementation rather than of the problem. `SocketsHttpHandler.ConnectCallback` exposes it.

The provider's handler now opens the socket itself, reproducing the handler's own default (a TCP
socket with Nagle disabled) and doing nothing else differently, because the only reason to take this
over is to time it. The timing is stashed on the request that triggered the connection and read back
after the response arrives, so exactly the request that paid for a connection is charged for it —
ambient state would have charged whichever request happened to be nearby.

It is recorded as two boundaries, not a bare number, so the measurement is derived from observed
instants like every other phase. `ConnectTimeout` still applies to the callback, so phase attribution
is unchanged.

**Absent for a pooled connection**, which is the ordinary case after the first request. That absence
is the point: a zero would claim a connection was opened instantaneously, which is a measurement of
an event that never happened. Both halves are tested — the first request records the phase, the
second records neither boundary nor measurement.

Rejected: reporting a duration from the provider without boundaries. It would have been less code and
would have made this the one latency phase in the product that a reader cannot locate on a timeline.

### 14. Pooled buffers that held content are cleared on return

Every `ArrayPool` rental that carried a prompt or model output is returned with `clearArray: true`.

A pooled array outlives the exchange that filled it, and the classic way content escapes is a later
renter that trusts the array's length instead of its read count. The product's whole privacy posture
is that content does not linger where it was not authorised, and "it is only process memory" is the
argument every leak of this shape starts from.

The cost is what settled it: these buffers are rented once per exchange, not once per read, so
clearing is a single memset against a stream that ran for seconds. Had it been per-read it would have
deserved a harder look.

### 12. The OpenTelemetry SDK is deferred to Stage 1C

Three source comments and one architecture test said Stage 1B would replace the self-registered
`ActivityListener` with the SDK. `docs/ROADMAP.md` never did, and none of Stage 1B's exit criteria
needs an exporter. Adopting it here would also have meant running a second `ActivityListener` while
debugging a new streaming pump. The comments now name 1C, which is when persistence and the trace API
give an exporter a consumer.

Stage 1B still pays the debt that mattered: `agentsplice.stream` has a real `ActivitySource`, and a
contract test now asserts that every source the listener subscribes to has something that writes to
it. `agentsplice.persistence` stays declared but unsubscribed until 1C.

**Superseded by [ADR 0013](0013-stage-1c-metadata-store.md) decision 11.** Persistence shipped and the
reasoning above did not survive it: what the SDK adds is an exporter, and none is configured until
packaging. The deferral now names Stage 1D. `agentsplice.persistence` became live in 1C anyway,
because the metadata writer produces spans on it.

### 13. `AgentSplice.PerformanceTests` was not created

The roadmap deferred it to this stage, and the exit criterion it was meant to serve — "long streams
pass without full buffering" — is a correctness claim, not a performance one. It is proven
behaviourally instead: an 8 MiB stream succeeds with `maxUpstreamCompletionBodyBytes` set to 64 KiB,
which can only happen if the streaming path never routes through the buffered bound. That test cannot
flake and cannot be gamed.

Gateway-overhead numbers are hardware-dependent. On shared CI runners a wall-clock threshold is either
so loose it proves nothing or so tight it flakes, and `docs/BENCHMARKING.md` already forbids universal
claims from one machine. If a perf gate is ever added it should measure allocated bytes per operation,
which is deterministic across machines and is the number that actually protects the memory ceiling.

## Consequences

- `stream: true` is served. `OpenAiChatCompletionRequestCodec.StreamingUnsupportedMessage` is gone
  from the public surface, along with the tests that asserted it.
- `IModelRuntimeProvider` gained `StreamAsync`; `ProviderCompletionRequest` gained a required
  `AcceptMediaType`, so the buffered call site now states `application/json` rather than inheriting it.
- `ChatCompletionGateway.CompleteAsync` takes an `IClientResponseSink`. A streamed response is written
  as it arrives, so `ChatCompletionOutcome.ResponseAlreadyWritten` tells the transport to add nothing.
- `error.type` gained `rate_limit_error` and the core codes gained `agentsplice_gateway_overloaded`.
- `TelemetryNames.Stage1AInstruments` and `Stage1AAttributes` became `LiveInstruments` and
  `LiveAttributes`, and the corresponding `docs/OBSERVABILITY.md` headings became stage-neutral, so a
  later stage adding an instrument no longer renames a public set.

## Producible stream terminations

| Termination | Produced by | Proven by |
|---|---|---|
| `NotApplicable` | any exchange that never streamed | yes |
| `NormalCompletion` | clean EOF with no terminator | yes |
| `ProtocolTerminatorReceived` | `[DONE]` observed | yes |
| `ClientCancelled` | client disconnected mid-stream | yes |
| `Timeout` | idle or total budget elapsed after the response started | yes |
| `MalformedEvent` | unparsable payload, or EOF mid-event | yes |
| `ConnectionLost` | upstream reset mid-stream | yes |
| `LimitExceeded` | one event exceeded `maxStreamEventBytes` | yes |
| `Unknown` | a failure with no more specific classification | reachable, not asserted |
| `UpstreamCancelled` | — | **not producible in Stage 1B** |

`UpstreamCancelled` stays in the vocabulary and unreachable. Over HTTP/1.1 a runtime aborting its own
response is a TCP reset, byte-identical to a connection lost for any other reason. Faking the
distinction would be worse than admitting it; the member documents a distinction an HTTP/2 provider
could reach.

## Malicious-stream mitigations

`docs/THREAT_MODEL.md` names six behaviours under "malicious upstream stream". Each has a mitigation
and a test:

| Behaviour | Mitigation | Bounded by |
|---|---|---|
| endless frames | each event is released as soon as it is forwarded | `timeouts:total` |
| giant events | relay stops and abandons the response | `limits:maxStreamEventBytes` |
| malformed UTF-8 | never decoded, so it is a non-event by construction | — |
| malformed JSON | classified, relayed, recorded; never thrown | — |
| excessive nesting | the reader's own depth limit surfaces as a malformed event | `Utf8JsonReader` max depth |
| duplicate terminals | the first terminator ends the response; a later one is never read (ADR 0010) | — |
| slowloris | per-read budget, re-armed between reads | `timeouts:idleStream` |

Excessive nesting deserves its own note: an unbounded recursive parser is a stack overflow, and no
catch block contains one — the process dies, taking every other in-flight exchange with it. The depth
limit belongs to the JSON reader rather than to AgentSplice, so the test asserts the behaviour rather
than the mechanism.

## Known limitations

- `WebApplicationFactory` uses `TestServer`, so `DisableBuffering` is a no-op in every integration
  test. The suite proves AgentSplice flushed, not that Kestrel did. End-to-end flush behaviour under
  Kestrel is exercised by the manual local check in the release notes, not by CI.
- ~~A runtime that sends `[DONE]` and then holds the connection open is read until EOF or the idle
  budget, because reading to EOF is what lets the connection be reused. The terminator takes
  precedence when classifying, so this costs latency rather than accuracy.~~ **Superseded by ADR 0010
  decision 8.** It cost accuracy too: upstream completion was dated from whatever ended the
  transport, so the stall was absorbed into the upstream duration and the generation window derived
  from it. The first valid `[DONE]` now ends the relay, at the price of one connection per lingering
  runtime.
