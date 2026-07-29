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
- client disconnect.

### Trace and privacy

- timeline event ordering;
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
