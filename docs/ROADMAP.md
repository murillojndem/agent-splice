# Roadmap

The roadmap is outcome-based. Dates are intentionally omitted until implementation slices are estimated. The order reflects the durable product thesis: trace and evidence first, then replay and conformance, then evaluation, and only later optional compatibility adapters.

## Stage 0 — Repository foundation

Status: complete. See `CHANGELOG.md` and ADR 0007. `AgentSplice.PerformanceTests` remains deferred;
ADR 0009 records why a wall-clock benchmark project would not have earned its place in CI.

Deliverables:

- .NET solution and project boundaries;
- analyzers, nullable, formatting, and architecture tests;
- Windows and Ubuntu CI;
- deterministic fake upstream server;
- validated configuration objects;
- OpenAPI draft;
- Dockerfile and Docker Compose development stack;
- baseline ADRs, threat model, and privacy defaults.

Exit criteria:

- build and tests run on Windows and Linux;
- dependency boundaries are enforced;
- no production compatibility claim is made;
- a fake-upstream exchange can be represented by the domain model.

## Stage 1 — Transparent Trace Proxy

### Stage 1A — OpenAI-compatible LM Studio proxy

Status: complete. See `CHANGELOG.md` and ADR 0008. Pass-through routing is reachable through the
optional `agentsplice:defaultRuntimeId` setting; without it an unrecognised model is rejected.

- `GET /v1/models`;
- `POST /v1/chat/completions`, non-streaming;
- runtime endpoint configuration;
- model aliases;
- stable error translation;
- correlation IDs;
- structural request summaries;
- exact forwarding tests;
- content retention disabled by default.

Exit criteria:

- a standard client can list models and complete a non-streaming request through AgentSplice;
- forwarded fields are verified against the fake upstream;
- routing changes are represented as events;
- no vendor-specific response rewrite is required.

### Stage 1B — Streaming correctness and timeline

Status: complete. See `CHANGELOG.md`, ADR 0009, ADR 0010, ADR 0011, and ADR 0012. The relay forwards upstream
bytes verbatim and observes them as they pass; `AgentSplice.PerformanceTests` remains deferred,
because the no-full-buffering claim is proven behaviourally by an integration test and
gateway-overhead numbers are hardware-dependent and must not gate CI.

Three review passes over the finished 1A and 1B slices found ten correctness defects. The first pass
found five in the recorded evidence — collapsed boundary timestamps, a buffered first-byte boundary
that named body completion, whole-string media-type matching, a discarded `FlushResult`, and reading
on past the protocol terminator (ADR 0010, superseding parts of ADR 0009). The second found four more
in the framing and in the gap between the code and its own documentation — a per-event bound that a
completed event could slip past, a bound violation that retracted an already-delivered completion,
media-type parameters accepted unparsed, and a "verbatim" content type that was still being truncated
(ADR 0011). The third found one more, introduced by the second: protocol classification had been
wired to the relayable header, so a conforming event stream whose header was merely too long to
forward was served as a buffered response (ADR 0012).

Every pass turned up defects a green suite had been reporting as working code, twice because a test
asserted the defective behaviour as the contract. Every correction ships with a test verified to fail
against a deliberately reintroduced copy of the defect it covers.

- incremental SSE parser and writer;
- `ResponseHeadersRead` upstream client;
- cancellation and disconnect propagation;
- separate connect, headers, idle, and total timeout phases;
- first-byte, first-decoded-event, first-semantic-event, first-client-event, and completion
  timestamps, each read from the clock at the operation it names;
- malformed, truncated, split-byte, and multiline SSE fixtures;
- bounded memory and gateway-overhead measurements.

Exit criteria:

- long streams pass without full buffering;
- cancellation propagates reliably;
- the exchange timeline is reconstructable;
- prompt-processing and generation metrics are never conflated.

### Stage 1C — Metadata persistence, administrative APIs, and the minimal dashboard

Status: complete. See `CHANGELOG.md` and ADR 0013.

Five slices, and three of them shipped corrections found by review rather than by building. The write
path alone yielded a queue whose drops were silent, a registration-time configuration read that
disagreed with the rest of the host, and a shutdown token that discarded evidence already off the
queue. A directed review then found that bounding a structural summary's length and count had been
treated as making it safe, which it is not — a client picks the value of `role` and the name of every
property it sends — and that every persisted row claimed nothing had been retained. A follow-up found
that the first fix for the former had started normalising `" User "` to `user`, persisting a corrected
copy of the protocol as though it were the observation.

- SQLite metadata store;
- PostgreSQL-compatible persistence contracts;
- exchange list/detail administrative APIs;
- timeline event APIs;
- runtime health and model catalog APIs;
- retention policies;
- explicit content-retention opt-in;
- administrative authentication for non-loopback bindings;
- minimal React dashboard with Overview, Exchanges, Exchange Detail, and Runtimes.

The dashboard was built last on purpose: CLAUDE.md admits it only after the trace API is stable, and
building it against the finished API is what proved the API is usable rather than merely published.
It is a client of the documented endpoints and nothing else, which is the constraint that keeps it
honest — a field it needs has to be a field the contract declares.

Deferred from this stage:

- the OpenTelemetry SDK, moved to Stage 1D by ADR 0013: what it adds is an exporter, and none is
  configured until packaging.

Exit criteria:

- a user can inspect what was sent structurally, where time was spent, and how the stream ended;
- raw prompt/response content remains unavailable unless explicitly enabled and sanitized;
- persistence failure does not corrupt an active stream.

### Stage 1D — Local packaging and first public alpha

- Docker image;
- Compose example using `host.docker.internal` for LM Studio;
- direct-process setup;
- loopback-only defaults;
- release workflow;
- sample OpenCode, Cline, and generic client configurations;
- first dated trace report;
- documentation and demo capture.

Exit criteria:

- local installation is reproducible;
- Windows is a first-class path;
- one real client/runtime interaction is captured without semantic normalization;
- first public alpha is clearly labeled experimental.

## Stage 2 — Replay and Conformance

### Stage 2A — Sanitized replay artifacts

- sanitization pipeline;
- immutable replay artifacts;
- integrity hashes;
- exact versus adapted replay modes;
- credential stripping;
- replay target allowlists;
- concurrency, cancellation, and timeout controls;
- replay APIs and dashboard flow.

Exit criteria:

- a captured exchange can be safely replayed against its original target;
- adapted replay records every changed field or policy;
- replay never executes client-side tools.

### Stage 2B — Differential comparison

- multi-target replay;
- structured response diff;
- SSE event-sequence diff;
- tool-call diff;
- latency waterfall comparison;
- usage-provenance comparison;
- comparison report export.

Exit criteria:

- the same artifact can be compared across at least two model/runtime configurations;
- reports distinguish identical, structurally equivalent, incompatible, and inconclusive results.

### Stage 2C — Protocol and streaming conformance

- OpenAI Chat Completions suite;
- SSE suite;
- cancellation and timeout suite;
- usage and finish-reason suite;
- unknown-field behavior suite;
- compatibility-matrix evidence rules;
- machine-readable report format.

Exit criteria:

- support claims are backed by dated suite results;
- HTTP 200 alone cannot mark a combination as verified;
- fast suites run in CI against the fake upstream.

### Stage 2D — Tool-call conformance

- native structured tool calls;
- multiple and streaming calls;
- argument JSON and JSON Schema validation;
- ID continuity;
- tool-result lifecycle;
- ordinary-prose false-positive corpus;
- explicit unsupported results.

Exit criteria:

- tool support is reported independently from basic chat support;
- a model printing tool syntax is not marked structured-tool compatible.

### Stage 2E — Cache and runtime evidence diagnostics

- cache evidence model;
- repeated-prefix fixtures;
- cold/warm classification;
- runtime-log import adapters;
- slot/checkpoint evidence where observable;
- confidence-labeled `probable_hit`, `partial_hit`, `probable_miss`, `cold`, and `unknown` results.

Exit criteria:

- no report claims a cache hit without evidence;
- runtime-log parsing remains optional and isolated from proxy correctness.

## Stage 3 — Agent Evaluation and Regression

### Stage 3A — Evaluation scenario model

- immutable scenario schema;
- repository fixture reference;
- allowed tools and network policy;
- timeout and iteration limits;
- deterministic assertions;
- environment fingerprint;
- result provenance.

### Stage 3B — Sandboxed coding-task runner

- disposable Docker workspaces;
- synthetic or open-source repositories;
- build and test command execution;
- file-change capture;
- prohibited-path detection;
- no use of confidential employer code;
- task-level success/failure classification.

### Stage 3C — Agent/client comparison

- OpenCode, Cline, Aider, or custom harness adapters;
- common task fixtures;
- model/runtime matrix;
- tool-call validity;
- iterations and wall-clock time;
- tests passed;
- unauthorized changes;
- failure-layer classification.

### Stage 3D — Regression history and CI gates

- baseline promotion;
- version-to-version comparison;
- relative performance thresholds;
- correctness regression gates;
- report artifacts;
- scheduled hardware runs;
- alerting hooks.

Exit criteria for Stage 3:

- at least one complete coding task is reproducible across two agent/model/runtime combinations;
- task success is measured by assertions, not subjective output quality alone;
- regressions can be detected between software versions.

## Stage 4 — Optional Interoperability Adapters

### Stage 4A — Anthropic protocol support

- Messages request/response models;
- streaming content-block state machine;
- tool-use and tool-result mapping;
- explicit translation-loss report;
- Anthropic conformance suite.

### Stage 4B — Profile-driven tool-call recovery

- generic content-JSON adapter;
- Laguna XML adapter;
- Qwen-specific profile rules where evidence requires them;
- bounded streaming assembly;
- strict false-positive corpus;
- transformation events;
- passthrough/reject/annotate policies;
- adapter retirement criteria.

### Stage 4C — Prompt and tool-schema compaction

- deterministic whitespace and duplicate-definition reduction;
- description and example policies;
- token-estimation adapters;
- semantic-equivalence fixture corpus;
- per-profile opt-in;
- before/after trace and performance evidence.

### Stage 4D — Model and runtime support packs

- versioned profiles;
- fixtures;
- known limitations;
- conformance reports;
- benchmark baselines;
- evidence provenance;
- community validation workflow.

Exit criteria for Stage 4:

- adapters are demonstrably optional;
- the trace/replay/conformance core works with adapters disabled;
- every adapter has an upstream status and retirement policy.

## Stage 5 — Ecosystem and Inference Laboratory

### Stage 5A — OpenCode and Cline integrations

- provider setup helpers or plugins;
- diagnostic IDs surfaced in clients;
- trace links;
- optional compact-profile negotiation;
- no permanent fork unless required for research;
- upstream PRs where behavior belongs in the client.

### Stage 5B — Backend comparison laboratory

- ROCm environment capture;
- Vulkan environment capture where supported;
- CUDA and other community result import;
- fixed model/quantization scenarios;
- prompt processing, generation, TTFT, memory, stability, and task-success reports;
- unsupported configurations retained in reports.

### Stage 5C — Upstream contribution program

- issue bundle generator;
- minimal reproduction repositories;
- accepted issue/PR index;
- llama.cpp tests and patches;
- client/runtime documentation fixes;
- adapter deprecation after upstream fixes.

### Stage 5D — Adapter SDK and community ecosystem

- versioned adapter contracts;
- profile schema tooling;
- fixture validation CLI;
- signed or checksummed distribution metadata;
- compatibility report catalog;
- security review process.

## Deferred possibilities

- OpenAI Responses API;
- MCP observation or policy plane;
- multi-tenant hosted service;
- distributed benchmark workers;
- Kubernetes deployment;
- encrypted content vault;
- dynamic plugin marketplace;
- semantic tool selection;
- cloud-provider cost routing.

Deferred items must not distort the local-first core or create premature infrastructure.
