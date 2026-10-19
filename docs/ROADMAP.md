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
  configured until packaging. Delivered in Stage 1D by ADR 0014.

Exit criteria:

- a user can inspect what was sent structurally, where time was spent, and how the stream ended;
- raw prompt/response content remains unavailable unless explicitly enabled and sanitized;
- persistence failure does not corrupt an active stream.

### Stage 1D — Local packaging and first public alpha

Status: complete. `v0.2.0-alpha`, the first public alpha, was tagged on 2026-10-18 and published on
2026-10-19 as an immutable pre-release, with its image at `ghcr.io/murillojndem/agent-splice:0.2.0-alpha`.
It delivered:

- the OpenTelemetry SDK and opt-in OTLP export of traces and metrics, with an optional Compose
  collector (ADR 0014);
- the gateway serving the dashboard, with the image building it and CI building and smoke-testing the
  image (ADR 0015);
- the image's own health check, which CI waits on (ADR 0016);
- the direct-process path: a self-contained Windows package that needs no Docker and no .NET
  installation, built and smoke-tested on Windows in CI, with every installation path documented in
  `docs/INSTALLATION.md` (ADR 0017);
- tagged releases as drafts carrying that package and its digest (ADR 0018);
- a sample OpenCode configuration and a client guide for OpenCode, Cline, and any OpenAI-compatible
  client, with a README quickstart, checked against the gateway's address, headers, settings, and
  error codes (ADR 0019);
- the Apache License 2.0, carried with the notices of what each artifact bundles, and a private channel
  for vulnerability reports (ADR 0020);
- a self-contained Linux package beside the Windows one, built and smoke-tested on Linux in CI, with a
  release's assets assembled and verified on every change (ADR 0021);
- the image saved as the archive a release carries, run from that archive the way the installation guide
  runs it, and pushed to GitHub Container Registry only when a person publishes the release, with
  release immutability on (ADR 0022);
- the first dated trace report: OpenCode through the Windows package to LM Studio with two models, its
  evidence as the administrative API answered it, and a contract test holding every report to its
  format (ADR 0023);
- .NET 10 LTS for every build, package, and image, before .NET 8's support ends on November 10, 2026,
  with one contract test holding the SDK, the target, the image's base, and every SDK a workflow
  installs to one version (ADR 0024);
- a stream's status committed with the runtime's first byte, so a runtime that fails before it is
  answered with the gateway's `504` or `502` and every correlation header, and an error code recorded
  only when an envelope carried it (ADR 0025);
- a runtime error carried inside a `200` recorded as the runtime's failure — on the record, the metrics,
  the span, and the dashboard — while `completed` keeps meaning that the transport cycle finished
  (ADR 0026);
- what a local gateway exposes, closed or said before it is public: only the loopback host names are
  served, a completion must be declared as JSON, a binding beyond loopback is announced, nothing a client
  chose leaves through trace context, lost evidence is counted, and the documents and settings claim
  only what the build does (ADR 0027);
- dashboard screenshots taken from the real gateway against a synthetic runtime, and the script that
  takes them (`docs/images/`).

The stage's original list, every item of it done:

- OpenTelemetry SDK and OTLP exporter, off by default (done);
- the gateway serves the built dashboard at `/dashboard/`, built into the Docker image and checked in
  CI, so an installation needs no Node toolchain to open it (done);
- Docker image with a health check that needs no HTTP client in the base image (done);
- Docker image published to a registry: pushed to GitHub Container Registry when a release is published
  (done, ADR 0022; first pushed with 0.2.0-alpha);
- Compose example using `host.docker.internal` for LM Studio (done);
- direct-process setup: from source anywhere, and self-contained Windows and Linux packages (done);
- loopback-only defaults, checked against the running package as well as the host (done);
- release workflow: a version tag becomes a reviewed draft pre-release carrying the Windows and Linux
  packages and one `SHA256SUMS` (done);
- sample OpenCode, Cline, and generic client configurations (done);
- .NET 10 LTS before the first release, so that no release carries a runtime whose support has ended
  (done, ADR 0024);
- close the streaming gap recorded in ADR 0019: a stream that times out or loses its runtime after the
  runtime's headers but before its first relayed byte must reach the client as more than a bare
  connection reset — the first trace report met it in a real session (done, ADR 0025: the status is
  committed with the first byte, and the failure is answered with the gateway's `504` or `502`);
- record an exchange's error code only when an envelope carried it to the client (done, ADR 0025), and
  every timeout phase and upstream failure on its timeline (done, ADR 0025);
- record a runtime error carried inside a `200` response as the runtime's failure, distinct from a
  success in the record, the metrics, and the dashboard, while `completed` keeps meaning that the
  transport cycle finished (found by the first trace report; done, ADR 0026);
- first dated trace report (done, ADR 0023);
- the pre-public batch the alpha-readiness audit of 2026-10-17 asked for (done, ADR 0027);
- documentation and demo capture (done: `docs/images/`, from synthetic data; a recorded session with a
  real client is the trace report's job).

Exit criteria:

- local installation is reproducible;
- Windows is a first-class path;
- one real client/runtime interaction is captured without semantic normalization;
- first public alpha is clearly labeled experimental.

All four were met when 0.2.0-alpha was published.

After the release, one change to Stage 1 behaviour, released in 0.2.1-alpha: the wait for a stream's
first byte has its own budget, `timeouts:firstByte`, and the response-header budget defaults to 5
minutes, so a long prompt on local hardware is no longer cut as a stalled stream (ADR 0028, from the
first trace report's finding 2). Stage 2A stays the stage in progress.

## Stage 2 — Replay and Conformance

### Stage 2A — Sanitized replay artifacts

Status: in progress. The current stage since 0.2.0-alpha, and the one `CLAUDE.md` names as the scope of
implementation work; nothing of it is implemented yet.

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
