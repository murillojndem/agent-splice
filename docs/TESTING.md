# Testing strategy

## Test projects

- Unit tests: domain rules, provenance, timelines, sanitizers, parsers, policies, and later adapters.
- Contract tests: public protocol and administrative API behavior.
- Integration tests: API plus fake upstream plus persistence.
- Architecture tests: dependency and adapter boundaries.
- Performance tests: gateway overhead, streaming allocation, and bounded queues.
- Conformance tests: versioned protocol and behavioral suites.
- Evaluation tests: scenario orchestration with deterministic small fixtures.

## Required Stage 1 fixture families

### OpenAI requests

- minimal chat;
- system/user/assistant messages;
- unknown fields;
- tools passed transparently;
- `tool_choice` passed transparently;
- streaming options;
- malformed input;
- model alias resolution;
- stable errors.

### SSE

- one event per read;
- event split byte by byte;
- UTF-8 character split across reads;
- multiple events per read;
- multiline data;
- comments/keepalive;
- CRLF and LF;
- malformed JSON;
- premature EOF;
- `[DONE]`;
- usage terminal chunk;
- duplicate terminal event;
- a runtime that stalls after the terminator;
- an event larger than its bound, both unterminated and completed in the same read;
- an oversized event behind the terminator in the same read;
- an event-stream content type carrying a `charset` parameter;
- a malformed content type that still names the right media type;
- a valid content type longer than the evidence bound;
- client disconnect;
- a client write that reports the client gone without throwing.

### Trace and privacy

- timeline event ordering;
- each boundary stamped at the operation it names, not at a shared instant;
- timestamps non-decreasing across the whole timeline;
- unknown timestamps remain absent;
- measured/reported/estimated provenance;
- body retention disabled by default;
- no prompt or response in default logs;
- headers sanitized;
- persistence failure behavior;
- trace and request ID correlation.

## Later conformance fixture families

### Tool calls

- native structured call;
- multiple calls;
- nested arguments;
- invalid argument JSON;
- unknown tool;
- missing/duplicate IDs;
- streaming fragments;
- tool-result continuity;
- ordinary prose containing examples;
- Unicode;
- adversarial delimiters;
- profile-gated text adapters.

### Replay

- exact artifact creation;
- credential removal;
- deterministic placeholders;
- integrity hash;
- target allowlist;
- exact versus adapted manifest;
- comparison categories;
- cancellation and timeout;
- no tool execution.

### Protocol translation

- lossless supported mapping;
- explicit translation loss;
- role/content ordering;
- tool lifecycle;
- streaming lifecycle;
- unsupported vendor extensions.

## Fake upstream server

`tests/AgentSplice.TestSupport` provides `FakeUpstreamServer`: a real Kestrel listener on an ephemeral loopback port that answers with scripted responses and records every request verbatim.

It is a real listener rather than an in-memory handler because streaming preservation, cancellation propagation, and timeout phases are properties of the transport. An in-memory handler cannot demonstrate that a client disconnect reached the runtime, nor that events were flushed rather than buffered.

Capabilities:

- `UpstreamResponseScripts` for JSON, status-only, malformed, and truncated responses;
- `SseScript` for byte-exact event streams, with LF or CRLF endings, multiline `data`, comments and keepalives, named events, `retry` directives, and the `[DONE]` sentinel;
- byte-level rechunking, including one byte at a time, so events split across arbitrary network and UTF-8 boundaries;
- per-event delays, delayed headers, and trailing stalls for the response-header and idle-stream timeout phases;
- connection reset for premature EOF;
- `ReceivedRequests` for exact-forwarding assertions;
- `RecordedUpstreamRequest.WaitForAbortAsync` for upstream-side cancellation evidence.

Response resolution is path-specific queue, then shared queue, then default. The out-of-the-box default is HTTP 404, so a test that forgot to script a call fails loudly instead of receiving an accidental 200.

The fixture is itself tested in `AgentSplice.IntegrationTests`. Every streaming, cancellation, and timeout test depends on it, so a fixture that silently buffered or completed cleanly where it was told to reset would let those tests pass while the gateway was broken.

## Log and trace capture fixtures

`tests/AgentSplice.TestSupport` also provides `CapturingLoggerProvider`, which records every log
message, structured state value, and active scope. The formatted message alone is not enough to prove
content never leaks: a structured value or a scope property reaches a sink just as surely.

The privacy suite runs with logging at `Trace`. The claim worth making is that content is absent even
at the most verbose setting, not that the default level filters it out — the weak form of that test
passes on a gateway that logs prompts at `Debug`.

Integration collections run serially. `WebApplicationFactory` resolves a top-level-statements entry
point through static handoff state shared across the process, so concurrent hosts let one factory
observe another's disposed provider, and tests that deliberately fail startup then see
`ObjectDisposedException` instead of the validation failure they assert.

## Delivered by Stage 1A

From the fixture families above: minimal chat, system/user/assistant messages, unknown fields, tools
and `tool_choice` passed transparently, malformed input, model alias resolution, stable errors,
timeline event ordering, unknown timestamps remaining absent, measurement provenance, body retention
disabled by default, no prompt or response in default logs, headers sanitised, and trace/request ID
correlation.

## Delivered by Stage 1B

Every SSE family: one event per read, an event split byte by byte, a UTF-8 character split across
reads, multiple events per read, multiline data, comments and keepalives, CRLF and LF, malformed
JSON, premature EOF, `[DONE]`, the usage terminal chunk, a duplicate terminal event, and client
disconnect. Each is asserted twice — once against the frame reader in isolation, and once end to end
through the gateway with an independent client-side parser.

## Delivered by Stage 1C, part 1

Persistence-failure behaviour, which Stage 1B recorded as still owed. It is asserted through a context
factory that refuses rather than a real database made to fail: breaking a file mid-run is
timing-dependent and platform-specific, while the property under test is the policy — the failure is
logged with a stable event ID, counted per exchange lost rather than per batch, the batch is dropped
rather than retried, the writer keeps draining, and nothing reaches the caller that produced the
evidence.

Alongside it: the row mapper's absence rules (an unobserved boundary produces no row, unreported usage
stays null rather than becoming zero, an estimate is never stored as measured), queue saturation
counted rather than silently dropped, and end-to-end assertions that a real proxied request reaches a
real SQLite file with no prompt or response content anywhere in it.

PostgreSQL Testcontainers are not used yet, because no PostgreSQL provider ships. They arrive with the
provider, and the SQLite tests stay regardless.

### Added by the Stage 1A/1B correctness reviews

The per-event bound is asserted at every shape that reaches it — unterminated, completed in the append
that crosses the ceiling, exactly on it, split across reads, and many small events whose total exceeds
it — because a bound enforced in the ordinary case and skipped in the interesting one looks exactly
like a bound that works. The interaction between the bound and the protocol terminator is asserted in
both directions: a violation behind `[DONE]` does not retract the completion, and one ahead of it
still ends the stream.

Media-type classification is asserted against the RFC 9110 grammar rather than against a list of
strings that happen to work, including the malformed values that name the right media type
(`text/event-stream; ===`) and the empty-parameter production the grammar permits
(`text/event-stream;`).

### Stage 1B fixture additions

`SseScript.Gate(UpstreamGate)` stops a scripted response at a chosen point until the test releases
it. Chunk delays are real wall-clock waits, so a test built on them is either slow or flaky and never
both; a gate replaces the guess with a fact, which is what makes per-event delivery, mid-stream
disconnect, and idle-timeout tests deterministic. It cannot be combined with re-chunking, because
re-chunking destroys the boundary the gate was placed at.

`SseScript.RawBytes` expresses a payload that is not valid UTF-8. A relay that decoded text cannot
forward those bytes unchanged, and a fixture that can only express strings cannot test whether it
does.

`SseClientReader` parses the gateway's output from the client's side. It is deliberately an
independent implementation rather than the gateway's own `SseFrameReader`: a test whose parser is the
subject's parser proves only self-consistency and keeps passing through any framing bug the two
share. It is the same principle that keeps the fake upstream free of production types.

A scripted response carrying both a whole body and a sequence of chunks is now refused. Previously
both were written, so a test that took a whole-body helper and added chunks received a payload it
never intended, with nothing to say so.

### Proving when a boundary was stamped

A timing boundary is a claim about *which operation* read the clock, and no test that runs against a
socket can pin that down: the fixture cannot make time pass between the first byte of a body and the
last on demand, and a test that waited for a real delay would be racing the reader rather than
asserting on it. Two of the five defects ADR 0010 corrects survived a green suite for exactly this
reason.

The boundary tests therefore drive the relay and the provider directly, with a controllable
`TimeProvider` and fakes that advance it *inside* the operation they model — a scripted upstream body
whose read costs twenty seconds, a client sink whose flush costs five. A boundary stamped before the
await and one stamped after it are then a wall apart rather than microseconds apart, and no
accidental pass can squeeze through. The clock also auto-advances on every reading, so two boundaries
can never share a timestamp by coincidence.

Where a boundary is asserted end to end, it is asserted as an ordering or an absence — never as an
approximate duration.

### Replaced Stage 1B contracts

Two Stage 1B tests asserted the defective behaviour and were rewritten rather than extended:

- the boundary-order test expected the decoded-event boundary before the client-flush boundary, which
  contradicted the relay's own write-then-decode structure;
- the duplicate-terminator test required both terminators to be relayed and three events recorded,
  which consolidated a runtime anomaly as the contract.

A test that encodes a defect is worse than a missing test: it makes the defect look deliberate.

## Testcontainers

Use PostgreSQL Testcontainers for persistence integration tests. SQLite tests remain required because behavior differs. Evaluation containers must use independent fixtures and explicit resource limits.

## Golden tests

Reviewed golden files may cover:

- protocol envelopes;
- SSE event sequences;
- trace timelines;
- sanitization reports;
- replay manifests;
- conformance reports;
- optional adapter output.

Golden updates require explicit review and must never be accepted automatically merely because output changed.

## Property and fuzz tests

Use bounded property/fuzz tests for SSE framing, JSON extension preservation, secret redaction, tool-call candidate parsing, and malformed input. Fuzz tests must have deterministic seeds in CI.

## Performance regression gates

Shared CI should gate gateway-only allocation and relative overhead against the fake upstream, not real-model latency. Hardware benchmarks run in labeled environments and publish raw results. Correctness regressions take precedence over throughput gains.
