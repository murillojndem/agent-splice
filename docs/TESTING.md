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

Implement a test-only controllable HTTP server with scenario endpoints and scripted timing. It must expose received requests so tests can verify exact forwarding. It should support malformed streams, delayed headers, delayed events, premature close, usage chunks, and cancellation observation.

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
