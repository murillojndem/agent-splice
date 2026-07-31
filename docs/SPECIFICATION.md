# AgentSplice — Complete Product and Engineering Specification

**Document status:** Foundational specification  
**Target readers:** maintainers, contributors, coding agents, reviewers, future employers, integration authors  
**Primary implementation language:** C# / .NET 8  
**Product form:** local-first modular monolith, extensible by provider and protocol adapters

---

## 1. Executive summary

AgentSplice is a local-first interoperability, observability, replay, conformance, and evaluation platform for AI agents. It sits between agent clients and model runtimes so the interaction can be captured, explained, reproduced, compared, and tested independently of either side.

The product is motivated by a recurring systems problem: an agent workflow is a chain of components, but failures are commonly attributed to “the model” even when the actual cause is a client schema, provider adapter, runtime parser, prompt template, stream implementation, cache mechanism, model behavior, quantization, software version, or hardware backend. AgentSplice makes those boundaries explicit and produces evidence at each boundary.

The durable core is not a workaround for one OpenCode/LM Studio incompatibility. It consists of:

1. transparent trace capture and timeline reconstruction;
2. privacy-safe observability with measurement provenance;
3. sanitized exact and adapted replay;
4. protocol, streaming, tool-call, cancellation, and usage conformance;
5. differential comparison across clients, models, runtimes, backends, profiles, and versions;
6. complete agent-task evaluation and regression testing;
7. portable reports and OpenTelemetry export.

Compatibility functions remain useful but are intentionally modular. Tool-call recovery, Qwen/Laguna-specific behavior, OpenAI/Anthropic translation, prompt/schema compaction, runtime-log parsing, and version-specific workarounds are implemented as optional adapters with evidence, fixtures, activation constraints, failure policies, and retirement criteria. When an upstream fix removes the need for an adapter, AgentSplice remains useful.

The first version is intentionally narrow. It exposes OpenAI-compatible model discovery and chat-completion endpoints, proxies requests to LM Studio, preserves non-streaming and SSE behavior, records a structural exchange timeline, emits OpenTelemetry data, stores privacy-safe metadata locally, and provides a minimal dashboard. It does not require semantic response rewriting.

Later stages add replay, conformance suites, differential comparison, cache-evidence diagnostics, agentic coding evaluations, protocol adapters, compatibility adapters, model/runtime support packs, client integrations, backend laboratories, and upstream contribution tooling.

The project serves three purposes simultaneously:

1. a useful local diagnostic and evaluation component;
2. a rigorous engineering portfolio demonstrating applied-AI systems work;
3. a reproducible laboratory for finding, proving, and upstreaming compatibility and performance defects across the open-source inference ecosystem.

## 2. Problem statement

### 2.1 Core problem

Agent clients assume that a model runtime behaves according to a protocol. In practice, compatibility exists on several separate layers:

- HTTP endpoint compatibility;
- request-schema compatibility;
- prompt-template compatibility;
- model behavioral compatibility;
- tool-call serialization compatibility;
- streaming-event compatibility;
- usage-metadata compatibility;
- cache compatibility;
- model/runtime/hardware compatibility.

A runtime can accept an OpenAI-shaped request and still be unsuitable for an agent because it emits tool calls as raw text, streams malformed argument fragments, ignores `tool_choice`, does not preserve cache checkpoints, or exposes performance characteristics that make agent loops impractical.

### 2.2 User-visible symptoms

Typical symptoms include:

- a request remains in prompt processing for minutes;
- the client shows no thinking because generation has not started;
- the model writes a correct XML or JSON tool invocation but the client sees ordinary text;
- a title-generation request blocks the actual task because the runtime has one prediction slot;
- tokens-per-second appears higher while end-to-end latency becomes worse;
- a fresh conversation remains slow because the client system prompt and tool schemas dominate context;
- changing speculative-decoding settings causes requests not to process;
- a model works in direct chat but fails through an agent;
- identical GGUF files behave differently under ROCm and Vulkan;
- the runtime reports model capabilities that are not actually operational.

### 2.3 Why existing clients, runtimes, and generic gateways do not eliminate the need

OpenCode, Cline, Aider, LM Studio, llama.cpp, Ollama, and model authors can and should fix defects within their own products. AgentSplice must not depend on those defects remaining unfixed.

Generic LLM gateways typically focus on provider routing, API keys, rate limits, budgets, fallbacks, and cloud-provider normalization. Individual agent clients focus on completing tasks, while individual runtimes focus on inference. None of those parties is naturally neutral across the entire matrix of competing clients, protocols, models, templates, runtimes, versions, quantizations, and hardware backends.

AgentSplice therefore focuses on independent evidence and cross-system behavior:

- capture of the actual client-to-runtime interaction;
- timeline reconstruction across protocol and streaming boundaries;
- exact and adapted replay;
- differential comparison;
- protocol and tool-call conformance;
- cache-evidence classification;
- complete agent-task evaluation;
- version-to-version regression history;
- local GPU/backend telemetry where available;
- exportable issue and pull-request evidence.

A runtime may eventually parse every known tool format correctly, and a client may eventually use compact, cache-stable requests. AgentSplice is still useful for proving that behavior, detecting regressions, comparing alternatives, and identifying which component caused a new failure.

AgentSplice may interoperate with generic gateways but must not duplicate their full provider-routing and commercial billing feature sets in the core roadmap.

## 3. Product vision

### 3.1 Vision statement

Make AI-agent interactions observable, reproducible, and testable across clients, models, runtimes, protocols, versions, and hardware backends.

### 3.2 Mission

Provide a transparent, local-first, vendor-neutral evidence plane that:

- captures what the client sent and what the runtime received;
- identifies where correctness and latency failures occur;
- reconstructs streaming and tool-call lifecycles;
- safely replays interactions against alternate targets;
- validates protocol and behavioral conformance;
- evaluates complete agent tasks with deterministic assertions;
- detects regressions before updates are promoted;
- enables open models and self-hosted runtimes to be compared fairly;
- gives maintainers minimal reproductions and upstream-quality evidence;
- treats commodity and non-CUDA hardware as first-class test environments.

### 3.3 Product analogy

AgentSplice should behave like a specialized combination of:

- Wireshark for agent/runtime traffic;
- Postman for replayable agent requests;
- a conformance laboratory for protocols and tools;
- an evaluation runner for complete agent tasks;
- an optional interoperability layer when explicit adaptation is justified.

### 3.4 Product principles

#### P-001 — Evidence before workaround

Capture and classify behavior before introducing a transformation. A workaround without a reproducible defect and test fixture is not acceptable.

#### P-002 — Transparent by default

Stage 1 forwarding must avoid semantic rewriting. Every material change must be attributable to routing, security, protocol correctness, or an explicitly selected adapter.

#### P-003 — Durable core, replaceable adapters

Trace, observability, replay, conformance, evaluation, and reports are core. Client-, model-, runtime-, and version-specific fixes are adapters.

#### P-004 — Local first

The default deployment works on a developer workstation with a direct .NET process or Docker Compose and SQLite. It binds to loopback by default and requires no cloud account.

#### P-005 — Safe by default

Raw prompts, responses, credentials, code, paths, and tool arguments are sensitive. Content persistence is disabled by default and sanitization occurs before storage or export.

#### P-006 — Runtime neutrality

LM Studio is the first provider, not an architectural dependency. Runtime-specific behavior belongs in provider or evidence adapters.

#### P-007 — Client neutrality

OpenCode and Cline are target integrations, not core dependencies. AgentSplice uses documented protocol surfaces and neutral trace identifiers.

#### P-008 — Measurement provenance

Every value must be identified as measured, reported, derived from logs, estimated, inferred with confidence, or unknown.

#### P-009 — Correctness before speed

A faster invalid stream or malformed tool call is a regression. Performance results never override protocol and task correctness.

#### P-010 — Upstream where appropriate

When a defect belongs in a client, runtime, template, or backend, AgentSplice should generate a minimal reproduction and upstream contribution. Adapters must have retirement criteria.

## 4. Goals and non-goals

### 4.1 Stage 1 goals — Transparent Trace Proxy

- Expose `GET /v1/models`.
- Expose `POST /v1/chat/completions`.
- Support non-streaming and streaming OpenAI-compatible requests.
- Proxy transparently to LM Studio.
- Preserve unknown fields where safe and practical.
- Preserve cancellation and client disconnect semantics.
- Use phase-specific timeout policies.
- Record request, routing, upstream, streaming, completion, and persistence timeline events.
- Distinguish gateway overhead, upstream headers, first byte, first semantic event, first client event, and total time.
- Record token/throughput values only with provenance.
- Emit OpenTelemetry traces, metrics, and structured logs.
- Store metadata in SQLite locally and support PostgreSQL-compatible contracts.
- Keep raw content retention disabled by default.
- Provide runtime health, model catalog, exchange list/detail, and timeline APIs.
- Provide a minimal local dashboard after the trace API is stable.
- Ship Docker and direct-process setup.
- Provide unit, integration, contract, architecture, privacy, and streaming tests.

### 4.2 Stage 2 goals — Replay and Conformance

- Create sanitized immutable replay artifacts.
- Support exact and adapted replay with explicit manifests.
- Compare one artifact across multiple targets.
- Diff protocol structures, stream sequences, tool calls, latency phases, usage, and errors.
- Add OpenAI Chat Completions conformance.
- Add SSE, cancellation, timeout, usage, and finish-reason suites.
- Add native structured tool-call conformance.
- Add cache-evidence diagnostics with confidence labels.
- Produce machine-readable compatibility reports.
- Add Replay, Conformance, and Compatibility Matrix dashboard screens.

### 4.3 Stage 3 goals — Agent Evaluation and Regression

- Define immutable evaluation scenarios.
- Execute synthetic or open-source coding tasks in disposable environments.
- Measure task success, builds/tests, tool validity, file scope, iterations, latency, and resources.
- Compare agent/client/model/runtime combinations on common fixtures.
- Maintain approved baselines and detect correctness or performance regressions.
- Export CI artifacts and scheduled hardware reports.

### 4.4 Stage 4 goals — Optional Interoperability Adapters

- Add Anthropic-compatible Messages support where semantics are explicit.
- Add profile-driven text-to-tool recovery for selected encodings.
- Add deterministic prompt and tool-schema compaction.
- Add model/runtime support packs for Laguna, Qwen, and selected families.
- Record every adapter invocation and transformation.
- Keep all adapters optional and retire them when upstream fixes apply.

### 4.5 Stage 5 goals — Ecosystem and Backend Laboratory

- Build OpenCode and Cline integrations.
- Compare ROCm, Vulkan, CUDA, CPU, and other backends under controlled scenarios.
- Generate issue bundles and minimal reproductions.
- Submit upstream tests, fixes, and documentation improvements.
- Provide an adapter/profile contribution workflow.

### 4.6 Explicit non-goals

- Hosting or downloading model weights.
- Training foundation models.
- Executing shell, filesystem, browser, or MCP tools inside the core gateway.
- Becoming an IDE or chat product.
- Replacing agent clients or inference runtimes.
- Becoming a general cloud-provider billing/router platform.
- Providing semantic memory or RAG in the initial stages.
- Providing multi-tenant SaaS controls in the first releases.
- Claiming lossless translation between arbitrary protocols.
- Inferring cache hits without evidence.
- Guaranteeing arbitrary text-to-tool conversion.
- Hiding defects through silent fallback.
- Preserving obsolete adapters after their retirement conditions are met.

## 5. Personas and use cases

### 5.1 Local AI developer

Runs LM Studio or llama.cpp on a workstation, connects a coding agent, and needs reliable tools, visible latency metrics, and model-specific configuration.

Primary needs:

- simple setup;
- no mandatory cloud account;
- understandable failures;
- safe local storage;
- profiles that can be shared;
- comparison between model/runtime configurations.

### 5.2 Open-source maintainer

Receives bug reports involving a client, model, runtime, and GPU backend. Needs a minimal reproduction and normalized traces without private source code.

Primary needs:

- deterministic replay;
- sanitized export bundle;
- exact software and hardware metadata;
- event timelines;
- request/response protocol validation;
- regression tests.

### 5.3 Applied AI engineer

Builds agentic applications against multiple local and hosted runtimes. Needs one stable API and detailed compatibility data.

Primary needs:

- aliases and routing;
- capability discovery;
- transformation policies;
- protocol translation;
- observability;
- integration tests.

### 5.4 Performance engineer

Compares throughput and latency across models, quantizations, context lengths, cache settings, speculative decoding, and GPU backends.

Primary needs:

- controlled benchmark scenarios;
- warm/cold separation;
- prompt-processing and generation metrics;
- GPU and host memory observations;
- result versioning;
- statistical summaries.

### 5.5 Agent-client contributor

Works on OpenCode, Cline, or another client and needs to know whether a failure originates in the client or upstream stack.

Primary needs:

- raw versus normalized event views;
- stable mock provider;
- conformance fixtures;
- stream-fragment simulation;
- tool-call edge cases.

---

## 6. High-level architecture

AgentSplice begins as a modular monolith with explicit boundaries between durable core modules and optional adapters.

```text
Client
  OpenCode / Cline / Aider / custom harness
          |
          v
Ingress protocol adapter
  - OpenAI Chat Completions
  - Anthropic Messages (Stage 4)
          |
          v
Transparent exchange pipeline
  - validation and request limits
  - correlation and trace context
  - runtime/model resolution
  - safe structural observation
  - explicit routing events
          |
          v
Provider abstraction
  - LM Studio
  - llama.cpp / Ollama / vLLM / SGLang (later)
          |
          v
Model runtime and backend
          |
          v
Response and evidence pipeline
  - incremental stream decoding
  - timeline observations
  - usage/timing provenance
  - client response forwarding
  - optional adapter chain (later)
  - metadata persistence and OpenTelemetry
          |
          v
Client-compatible response
```

### 6.1 Durable core modules

#### API module

Owns hosting, endpoint routing, authentication integration, request limits, exception mapping, health endpoints, compatibility headers, and administrative APIs.

#### Application module

Owns use cases such as list models, create exchange, retrieve timeline, create replay artifact, run conformance suite, execute evaluation, and generate comparison report.

#### Domain module

Owns provider-neutral concepts:

- RuntimeEndpoint;
- ModelDescriptor and ModelAlias;
- CompletionExchange;
- ExchangeObservation;
- Measurement and MeasurementProvenance;
- StreamTermination;
- ReplayArtifact and AdaptationManifest;
- ReplayRun and DifferentialComparison;
- ConformanceSuite, ConformanceCase, and ConformanceRun;
- EvaluationScenario and EvaluationRun;
- CompatibilityDeclaration;
- AdapterInvocation.

#### Protocol modules

Own external DTOs, serialization, validation, and event mapping. OpenAI Chat Completions is first. Anthropic Messages arrives later.

#### Provider modules

Own upstream communication and provider-specific behavior. Stage 1 implements LM Studio.

#### Observability module

Owns ActivitySource, Meter, semantic conventions, event timing, provenance, and safe diagnostic enrichment.

#### Persistence module

Owns metadata storage, configuration/environment snapshots, replay artifacts, conformance results, evaluation results, and retention.

#### Replay module — Stage 2

Owns sanitization, immutable artifacts, replay orchestration, adaptation manifests, and comparisons.

#### Conformance module — Stage 2

Owns versioned suites, cases, assertions, support-declaration rules, and report output.

#### Evaluation module — Stage 3

Owns scenario orchestration, isolated task workers, assertions, and regression baselines.

### 6.2 Optional adapter modules

Adapters may implement:

- protocol translation;
- text-to-tool recovery;
- prompt/schema compaction;
- model-family profiles;
- runtime-log ingestion;
- backend telemetry;
- client integration metadata.

Adapters must not be referenced from Domain and must comply with ADR 0006.

## 7. Stage 1 functional requirements — Transparent Trace Proxy

Requirement identifiers are stable references for issues, commits, tests, and pull requests.

### 7.1 Exchange capture and timeline

**FR-TRACE-001** Every accepted completion request shall create a unique exchange ID and trace ID.

**FR-TRACE-002** The exchange shall identify ingress protocol, streaming mode, selected runtime, client-visible model, upstream model, and status.

**FR-TRACE-003** Stage 1 shall create safe structural summaries without retaining raw content by default.

**FR-TRACE-004** Timeline observations shall be immutable, sequence-ordered, and timestamped by `TimeProvider`.

**FR-TRACE-005** Supported observations shall include request accepted, validation complete, model resolved, upstream started, upstream headers, first upstream byte, first semantic event when observable, first client event, upstream completed, client completed/cancelled, timeout, and persistence result.

**FR-TRACE-006** Missing evidence shall remain absent or unknown; the gateway shall not fabricate timing boundaries.

**FR-TRACE-007** Routing changes, including model aliases, shall produce explicit events.

**FR-TRACE-008** Raw-versus-forwarded structural differences shall be represented without exposing sensitive content.

**FR-TRACE-009** Exchange APIs shall support pagination, bounded filters, and stable ordering.

**FR-TRACE-010** Content-retention state shall be visible on every exchange.

### 7.2 Model discovery

**FR-MOD-001** The gateway shall expose `GET /v1/models` using an OpenAI-compatible response shape.

**FR-MOD-002** The response shall combine configured aliases with models discovered from enabled upstream runtimes.

**FR-MOD-003** Model discovery shall have a configurable cache duration and stale-cache policy.

**FR-MOD-004** Duplicate upstream model identifiers shall be disambiguated internally by runtime endpoint ID.

**FR-MOD-005** Client-visible aliases shall resolve deterministically to one runtime and upstream model ID.

**FR-MOD-006** Alias cycles and duplicates shall fail configuration validation.

**FR-MOD-007** Capability claims shall retain provenance: configured, discovered, probed, inferred, or unknown.

**FR-MOD-008** Capability probing shall be disabled by default in Stage 1.

**FR-MOD-009** Administrative diagnostics shall distinguish configured models from currently reachable models.

### 7.3 Chat completions

**FR-CHAT-001** Expose `POST /v1/chat/completions`.

**FR-CHAT-002** Accept non-streaming and streaming requests.

**FR-CHAT-003** Validate model resolution before opening the upstream request.

**FR-CHAT-004** Preserve unknown fields when forwarding if safe and practical.

**FR-CHAT-005** Unsupported properties shall follow explicit transparent or strict policy.

**FR-CHAT-006** Forward client cancellation and disconnect to the upstream request.

**FR-CHAT-007** Apply separate connect, response-header, idle-stream, and total-request timeouts.

**FR-CHAT-008** Identify timeout phase without exposing secrets or internal network details.

**FR-CHAT-009** Return the AgentSplice request/exchange identifier in headers.

**FR-CHAT-010** Retain upstream request identifiers when available.

**FR-CHAT-011** Do not retry a streaming completion after bytes were sent.

**FR-CHAT-012** Non-streaming retries shall be disabled by default and limited to explicit transport policies when introduced.

**FR-CHAT-013** Model-generated content shall never be interpreted as gateway configuration.

**FR-CHAT-014** Stage 1 shall not reinterpret text content as structured tool calls.

**FR-CHAT-015** Valid native structured tool-call fields shall be forwarded as protocol data where the provider supports them.

### 7.4 Streaming

**FR-STR-001** Streaming responses shall use valid `text/event-stream` semantics.

**FR-STR-002** Preserve event order.

**FR-STR-003** Flush complete events with bounded delay.

**FR-STR-004** Support events split across arbitrary network and UTF-8 boundaries.

**FR-STR-005** Support multiline SSE `data` fields, comments, keepalive events, and CRLF/LF variants.

**FR-STR-006** Distinguish SSE framing from JSON payload framing.

**FR-STR-007** Stage 1 malformed-stream policy shall terminate with a stable error/observation; it shall not silently repair semantic content.

**FR-STR-008** Do not accumulate the complete response to calculate metrics.

**FR-STR-009** Recognize `[DONE]` only according to the active protocol. The first valid terminator is the logical end of the response: once its bytes have been delivered and the event recognized, no further upstream read shall be issued, upstream completion shall be timestamped at that recognition, and a later terminator shall be neither read nor forwarded (ADR 0010).

**FR-STR-010** Preserve usage terminal chunks when requested and supported.

**FR-STR-011** Distinguish normal completion, client cancellation, upstream cancellation, timeout, malformed event, and connection loss.

**FR-STR-012** Record first upstream byte, first decoded event, first semantic event when observable, and first client flush separately. Separately means four independent clock readings, each taken at the operation it names: the read that returned bytes, the frame reader returning a complete frame, the interpreter classifying output, and the completion of the write that delivered the first non-comment event. A shared timestamp satisfies the letter of this requirement and defeats its purpose (ADR 0010).

**FR-STR-013** A comment or keepalive frame may set the first-decoded-event boundary and shall not set the first-client-event boundary: a conforming client raises no event for it.

**FR-STR-014** Stream media-type classification shall follow RFC 9110: the media type is compared case-insensitively and parameters are ignored, so `text/event-stream; charset=utf-8` is an event stream. Classification shall not alter the content type forwarded to the client.

### 7.5 Measurements, provenance, and OpenTelemetry

**FR-OBS-001** Every exchange shall create an OpenTelemetry trace.

**FR-OBS-002** Metrics shall cover count, active exchanges, errors, cancellations, latency phases, stream bytes/events, usage, and persistence failures.

**FR-OBS-003** Prompt and completion token values shall retain their source.

**FR-OBS-004** Throughput shall be calculated only when token count and duration evidence are sufficient.

**FR-OBS-005** Prompt throughput and generation throughput shall remain separate.

**FR-OBS-006** Metric dimensions shall be bounded; prompts, arbitrary model IDs, paths, and request IDs shall not be metric labels.

**FR-OBS-007** Logs shall be structured and correlated without body content by default.

**FR-OBS-008** Hardware metadata collection shall be optional.

**FR-OBS-009** Runtime-log parsing shall be optional, versioned, and isolated from proxy correctness.

**FR-OBS-010** Estimated or inferred values shall be visibly labeled.

### 7.6 Persistence and privacy

**FR-DATA-001** Purely ephemeral proxy operation shall be possible without a database.

**FR-DATA-002** SQLite shall be the default local database when persistence is enabled.

**FR-DATA-003** PostgreSQL shall be supported through common persistence contracts.

**FR-DATA-004** Metadata and optional content bodies shall be separate artifact classes.

**FR-DATA-005** Raw request and response content storage shall be disabled by default.

**FR-DATA-006** Sanitization shall occur before content persistence or export.

**FR-DATA-007** Retention shall be configurable by artifact category.

**FR-DATA-008** Deletion jobs shall be idempotent and auditable.

**FR-DATA-009** Persistence failure shall not corrupt or indefinitely block active streaming.

**FR-DATA-010** Credentials and secret configuration shall never be stored in exchanges or replay artifacts.

### 7.7 Stage 1 administrative APIs and dashboard

**FR-DASH-001** Expose runtime health, model catalog, exchange list/detail, timeline, and observation APIs under `/api/v1`.

**FR-DASH-002** The dashboard shall be optional and consume documented APIs only.

**FR-DASH-003** Initial screens shall include Overview, Exchanges, Exchange Detail, and Runtimes.

**FR-DASH-004** Exchange Detail shall show a latency waterfall and measurement provenance.

**FR-DASH-005** The dashboard shall not display raw content unless content retention is explicitly enabled and authorized.

**FR-DASH-006** Unknown values shall be displayed as unknown, not zero.

### 7.8 Health and diagnostics

**FR-HEALTH-001** Expose liveness and readiness endpoints.

**FR-HEALTH-002** Liveness shall not depend on upstream availability.

**FR-HEALTH-003** Readiness may require an enabled reachable runtime by configuration.

**FR-HEALTH-004** Runtime health shall distinguish unreachable, authentication failure, incompatible response, and no models.

**FR-HEALTH-005** A diagnostic endpoint shall expose build version, enabled modules, supported protocols, and a redacted configuration summary.

**FR-HEALTH-006** Administrative diagnostics shall require authentication when bound beyond loopback.

## 8. Stage 2 functional requirements — Replay and Conformance

### 8.1 Replay artifacts

**FR-REPLAY-001** Replay shall use sanitized immutable artifacts rather than arbitrary live database rows.

**FR-REPLAY-002** Artifacts shall never include original credentials or unrestricted secrets.

**FR-REPLAY-003** Sanitization shall produce a report and version identifier.

**FR-REPLAY-004** Artifacts shall include an integrity hash and expiration policy.

**FR-REPLAY-005** Exact and adapted replay shall be distinct domain modes.

**FR-REPLAY-006** Adapted replay shall record every changed target, field, or policy.

**FR-REPLAY-007** Replay shall never execute client-side tools.

**FR-REPLAY-008** Workers shall enforce target allowlists, concurrency, rate limits, cancellation, and timeouts.

### 8.2 Differential comparison

**FR-DIFF-001** One artifact may be replayed against multiple targets.

**FR-DIFF-002** Comparisons shall evaluate protocol structure, stream order, tool calls, finish reasons, latency phases, usage provenance, cache evidence, and errors independently.

**FR-DIFF-003** Results shall distinguish identical, structurally equivalent, comparable, incompatible, and inconclusive outcomes.

**FR-DIFF-004** Text similarity alone shall not determine tool/protocol equivalence.

**FR-DIFF-005** Reports shall include environment and adaptation manifests.

### 8.3 OpenAI and SSE conformance

**FR-CONF-001** Conformance suites and cases shall be versioned and immutable after publication.

**FR-CONF-002** Results shall support pass, fail, skipped, unsupported, and inconclusive.

**FR-CONF-003** OpenAI Chat Completions cases shall cover request roles, unknown fields, tools, errors, usage, finish reasons, cancellation, and timeouts.

**FR-CONF-004** SSE cases shall cover split reads, UTF-8 boundaries, multiline data, comments, terminal events, malformed JSON, and premature EOF.

**FR-CONF-005** A support claim shall reference a dated run and environment snapshot.

**FR-CONF-006** HTTP 200 alone shall never produce a Verified compatibility declaration.

### 8.4 Native tool-call conformance

**FR-TOOLCONF-001** Test native structured single and multiple calls.

**FR-TOOLCONF-002** Validate tool names, argument JSON, optional JSON Schema, call IDs, ordering, and tool-result continuity.

**FR-TOOLCONF-003** Test streaming argument fragments and termination behavior.

**FR-TOOLCONF-004** Report basic chat and structured-tool support independently.

**FR-TOOLCONF-005** A model printing tool syntax shall not count as native structured-tool support.

### 8.5 Cache evidence diagnostics

**FR-CACHE-001** Distinguish gateway response caching from model prefix/KV reuse.

**FR-CACHE-002** AgentSplice shall not cache generated responses by default.

**FR-CACHE-003** Cache diagnostics shall classify only observable evidence.

**FR-CACHE-004** Evidence may include timings, processed-token counts, slot metadata, provider headers, and runtime-log events.

**FR-CACHE-005** Classifications shall be `cold`, `probable_hit`, `partial_hit`, `probable_miss`, or `unknown`, with confidence.

**FR-CACHE-006** Cache classification shall not alter response behavior.

### 8.6 Compatibility reports

**FR-REPORT-001** Reports shall include exact software, model, runtime, backend, quantization, context, and configuration evidence.

**FR-REPORT-002** Allowed statuses are Verified, Partially verified, Experimental, Broken, Unknown, and Unsupported.

**FR-REPORT-003** Unsupported and failed configurations shall remain visible.

**FR-REPORT-004** Reports shall be exportable in human- and machine-readable forms.

## 9. Later-stage functional requirements

### 9.1 Stage 3 — Agent evaluation

**FR-EVAL-001** Evaluation scenarios shall be immutable and versioned.

**FR-EVAL-002** Coding evaluations shall use synthetic or open-source repositories in disposable environments.

**FR-EVAL-003** A scenario shall define instruction, allowed tools, forbidden paths, network policy, time/turn limits, build/test commands, and hidden assertions.

**FR-EVAL-004** Results shall record task success, tests, tool validity, iterations, files changed, prohibited changes, timing, usage, failures, and intervention.

**FR-EVAL-005** The core gateway shall not execute evaluation tools; a separately permissioned worker shall perform approved sandbox actions.

**FR-EVAL-006** Agent/client/model/runtime combinations shall be comparable on identical fixtures.

**FR-EVAL-007** Regression baselines shall detect correctness and performance changes.

**FR-EVAL-008** Confidential employer code or incidents shall never be used as public fixtures.

### 9.2 Stage 4 — Anthropic protocol adapter

**FR-ANTH-001** Expose Anthropic-compatible Messages semantics only for explicitly supported mappings.

**FR-ANTH-002** Preserve message/content/tool lifecycle where mapping is lossless.

**FR-ANTH-003** Reject or report material translation loss.

**FR-ANTH-004** Streaming content-block events shall maintain lifecycle order.

**FR-ANTH-005** Every translated exchange shall produce a translation manifest.

### 9.3 Stage 4 — Optional compatibility adapters

**FR-ADAPT-001** Every adapter shall have stable ID/version, activation constraints, evidence, fixtures, failure policy, upstream status, review date, and retirement criteria.

**FR-ADAPT-002** Text-to-tool recovery shall be disabled unless an explicit profile selects it.

**FR-ADAPT-003** Ambiguous candidates shall never become executable calls by default.

**FR-ADAPT-004** Recovered tools shall be validated against supplied tools and argument JSON/Schema.

**FR-ADAPT-005** Ordinary prose that discusses tool syntax shall remain prose.

**FR-ADAPT-006** Streaming recovery shall use bounded state and shall not expose invalid intermediate calls.

**FR-ADAPT-007** Prompt/schema compaction shall be deterministic and opt-in.

**FR-ADAPT-008** Compaction shall not silently remove required schema constraints.

**FR-ADAPT-009** Before/after token estimates and semantic-equivalence fixtures shall be retained.

**FR-ADAPT-010** Adapters shall be deactivated for fixed upstream versions when evidence supports retirement.

### 9.4 Stage 4 — Support packs

**FR-PACK-001** A support pack shall contain profiles, adapters, fixtures, limitations, conformance reports, environment evidence, and upstream status.

**FR-PACK-002** Laguna packs may include observed XML tool encodings.

**FR-PACK-003** Qwen packs shall distinguish family, dense/MoE/MTP variant, template, quantization, runtime, and backend evidence.

**FR-PACK-004** Community packs shall pass schema and fixture validation.

### 9.5 Stage 5 — Client integrations and backend laboratory

**FR-INT-001** OpenCode/Cline integrations shall surface AgentSplice trace/exchange IDs and configuration helpers.

**FR-INT-002** Integrations shall avoid permanent forks when supported extension points exist.

**FR-INT-003** Backend reports shall treat ROCm, Vulkan, CUDA, CPU, and other implementations as distinct environments.

**FR-INT-004** Comparisons shall include correctness, prefill, generation, TTFT, total task time, stability, memory, and unsupported operations.

**FR-INT-005** Unsupported configurations shall be reported, not omitted.

**FR-INT-006** AgentSplice shall generate minimal reproduction and issue bundles suitable for upstream work.

## 10. API behavior

### 10.1 Base URL and versioning

Stage 1 uses OpenAI-compatible paths under `/v1`. Gateway-specific administrative APIs use `/api/v1` to avoid pretending that non-standard endpoints are part of the OpenAI contract.

```text
/v1/models
/v1/chat/completions
/api/v1/system
/api/v1/health/runtimes
/api/v1/exchanges
/api/v1/exchanges/{id}
/api/v1/exchanges/{id}/timeline
```

The exact administration surface may be implemented after the proxy path is stable.

### 10.2 Request identity

Each accepted request receives:

- internal ULID or UUID;
- public request ID;
- trace ID;
- optional client-supplied correlation ID;
- optional upstream request ID.

Response header:

```http
x-agentsplice-request-id: req_...
```

The gateway must not reuse client IDs as database primary keys.

### 10.3 Error envelope

OpenAI-compatible endpoints use an OpenAI-shaped error envelope where feasible:

```json
{
  "error": {
    "message": "The selected runtime did not produce a valid SSE event.",
    "type": "upstream_protocol_error",
    "param": null,
    "code": "agentsplice_invalid_upstream_stream"
  }
}
```

Internally, errors are classified into:

- client validation;
- authentication;
- model resolution;
- routing/configuration;
- upstream connection;
- upstream authentication;
- upstream timeout;
- upstream status;
- upstream protocol;
- adapter failure (later stages);
- replay/conformance/evaluation failure (later stages);
- persistence;
- cancellation;
- internal defect.

### 10.4 Streaming forwarding and optional adaptation

Stage 1 streaming passes through these logical phases:

1. Read bytes incrementally.
2. Parse complete SSE frames.
3. Decode enough protocol structure to validate framing and record observations.
4. Forward the corresponding client event without semantic rewriting.
5. Record non-content measurements and provenance.
6. Flush according to bounded policy.

In later adapter stages, an explicitly selected response adapter may emit zero, one, or multiple client events. Adapter buffering must be bounded by bytes and elapsed time, measured separately from upstream latency, represented in the exchange manifest, and disabled when no adapter is selected.

---

## 11. Optional compatibility adapter design

This section describes Stage 4 behavior. It is not required for the first public alpha and must not leak into the transparent Stage 1 core. The trace/replay/conformance platform must operate correctly with every compatibility adapter disabled.

Adapters exist to test or temporarily bridge known incompatibilities. Each invocation is explicit, versioned, observable, and subject to retirement after an upstream fix.

### 11.1 Adapter pipeline

```text
Raw upstream content
  -> candidate detector
  -> parser
  -> tool-name resolver
  -> JSON argument parser
  -> optional schema validator
  -> confidence/policy decision
  -> target protocol encoder
```

### 11.2 Tool-call candidate types

- Native structured tool calls.
- OpenAI-like JSON inside content.
- XML-tagged tool calls.
- Model-family-specific delimiters.
- Mixed prose and tool candidate.
- Partial streaming candidate.

### 11.3 Adapter safety rules

The gateway must not convert this prose:

```text
The model should emit <tool_call>write...</tool_call> when writing a file.
```

into an executable tool call.

Signals supporting a valid candidate may include:

- candidate occupies the complete assistant action segment;
- delimiters match a profile exactly;
- tool name exists in request tools;
- arguments parse as JSON or profile-defined key/value structure;
- no contradictory natural-language prefix or suffix exists;
- streaming state has reached the profile-defined terminal delimiter.

### 11.4 Adapter failure policies

- `passthrough`: preserve content and record failure.
- `reject`: return a normalization error.
- `annotate`: preserve content and add diagnostic metadata where protocol allows.
- `strip-candidate`: prohibited as a default because it may destroy meaningful text.

### 11.5 Adapter interfaces

Conceptual C# contracts:

```csharp
public interface ICompatibilityAdapter
{
    string AdapterId { get; }
    Version AdapterVersion { get; }

    ValueTask<AdapterOutcome> ApplyAsync(
        AdapterContext context,
        CancellationToken cancellationToken);
}
```

Streaming adapters require a state object scoped to one response and must never use global mutable state.

---

## 12. Configuration model

Configuration sources, in increasing precedence:

1. Built-in defaults.
2. Application settings.
3. Environment variables.
4. Mounted profile files.
5. Command-line arguments.
6. Future administrative overrides.

Example:

```yaml
agentsplice:
  publicBaseUrl: http://localhost:5280
  # Optional. When set, a model identifier matching no alias and no discovered model is forwarded
  # unchanged to this runtime (ModelResolutionSource.PassThrough). Unset means such a request is
  # rejected, so the strict posture is the default.
  defaultRuntimeId: lmstudio-local
  persistence:
    mode: sqlite
    connectionString: Data Source=/data/agentsplice.db
  diagnostics:
    storeBodies: false
    storeHeaders: allowlist
  compatibility:
    # transparent (default) forwards fields AgentSplice does not model and records their names;
    # strict rejects them. 'adapted' is a Stage 4 capability and is not accepted here.
    unsupportedFields: transparent
  limits:
    # The non-streaming path buffers whole bodies so they can be forwarded verbatim, so both
    # directions are bounded. Reading stops at the limit plus one byte.
    maxRequestBodyBytes: 4194304
    maxUpstreamCompletionBodyBytes: 67108864
    maxCatalogueBodyBytes: 4194304
    # The streaming path retains only the event it is assembling, so this bound times
    # maxConcurrentCompletions is the whole memory ceiling of a streamed exchange.
    maxStreamEventBytes: 1048576
    maxConcurrentCompletions: 64
  runtimes:
    - id: lmstudio-local
      provider: lmstudio
      baseUrl: http://host.docker.internal:1234/v1
      apiKeyEnvironmentVariable: LM_STUDIO_API_KEY
      discovery:
        enabled: true
        cacheDuration: 00:00:30
  aliases:
    - id: local-coder
      runtimeId: lmstudio-local
      upstreamModelId: qwen3.6-27b-mtp
  capture:
    metadataEnabled: true
    contentEnabled: false
  adapters:
    enabled: false
```

Secrets must be referenced by environment-variable or secret-provider name, not embedded in profile files.

---

## 13. Data model

### 13.1 RuntimeEndpoint

- Id
- DisplayName
- ProviderType
- BaseUri
- AuthenticationReference
- Enabled
- DiscoveryPolicy
- TimeoutPolicy
- CreatedAt
- UpdatedAt

### 13.2 ModelAlias

- AliasId
- RuntimeEndpointId
- UpstreamModelId
- Enabled
- Priority
- CapabilityClaims with provenance
- Metadata

### 13.3 CompletionExchange

- ExchangeId
- PublicRequestId
- TraceId
- IngressProtocol
- StartedAt
- CompletedAt
- RuntimeEndpointId
- ClientModelId
- UpstreamModelId
- Streaming
- Status
- FailureClass
- StreamTermination
- ContentRetentionState
- EnvironmentSnapshotId

### 13.4 ExchangeObservation

- ObservationId
- ExchangeId
- Sequence
- ObservationType
- Timestamp
- Duration when applicable
- Source
- Confidence when inferred
- SafeDetails

### 13.5 Measurement

- MeasurementId
- ExchangeId or RunId
- Name
- Value
- Unit
- Provenance: measured, client-reported, upstream-reported, runtime-log, estimated, inferred
- Confidence
- StartedAt/EndedAt when applicable

### 13.6 AdapterDefinition and AdapterInvocation

Definition:

- AdapterId and Version
- AdapterType
- ActivationConstraints
- SupportedVersions
- FailurePolicy
- EvidenceReferences
- UpstreamStatus
- ReviewDate
- RetirementCriteria

Invocation:

- InvocationId
- ExchangeId
- AdapterId and Version
- Direction
- ActivationReason
- Outcome
- Duration
- SafeManifest

### 13.7 ReplayArtifact

- ArtifactId
- SourceExchangeId
- CreatedAt
- SanitizerVersion
- RequestProtocol
- SanitizedRequest
- ConfigurationSnapshot
- ContentClassification
- Expiration
- IntegrityHash

### 13.8 ReplayRun and DifferentialComparison

ReplayRun:

- ReplayRunId
- ArtifactId
- ReplayMode: exact or adapted
- TargetRuntime/Model/Profile
- AdaptationManifest
- ResultExchangeId
- Status

Comparison:

- ComparisonId
- SourceArtifactId
- TargetRunIds
- StructuralResult
- StreamResult
- ToolResult
- TimingResult
- UsageResult
- OverallClassification

### 13.9 Conformance entities

- ConformanceSuite
- ConformanceSuiteVersion
- ConformanceCase
- ConformanceCaseVersion
- ConformanceRun
- ConformanceCaseResult
- CompatibilityDeclaration
- EvidenceReference

### 13.10 Evaluation entities

- EvaluationScenario
- EvaluationScenarioVersion
- EvaluationRun
- EvaluationIteration
- EnvironmentSnapshot
- ToolExecutionObservation
- FileChangeObservation
- CorrectnessAssertion
- RegressionBaseline
- RegressionComparison

## 14. Non-functional requirements

### 14.1 Performance

**NFR-PERF-001** In passthrough mode, gateway processing overhead excluding network transfer should target a median below 5 ms for non-stream setup on a modern desktop, measured independently from upstream inference.

**NFR-PERF-002** Streaming forwarding should add a target median below 10 ms between receiving a complete upstream event and flushing the corresponding client event when no buffering normalizer is active.

**NFR-PERF-003** Metrics collection must not require storing content bodies.

**NFR-PERF-004** The gateway must use bounded channels or equivalent backpressure mechanisms for asynchronous internal pipelines.

**NFR-PERF-005** Memory consumption per active stream must be bounded by documented configuration.

These are engineering targets, not release claims, until benchmarks exist.

### 14.2 Reliability

- No process crash from malformed upstream JSON.
- Cancellation propagates promptly.
- Database outage must not corrupt an active stream; configurable persistence failures may degrade to metadata loss with visible diagnostics.
- Configuration is validated before readiness.
- Provider adapters use `HttpClientFactory`.
- Stream parsers handle arbitrary chunk boundaries.
- Every background service has shutdown and failure behavior.

### 14.3 Security

- Loopback binding by default.
- Authentication required by default when binding to non-loopback interfaces.
- Secrets excluded from logs and replay.
- Request-body limits.
- Header allowlists.
- SSRF prevention for administratively configured runtime endpoints.
- No arbitrary runtime URL supplied per completion request in default mode.
- Tool execution outside gateway scope.
- Sanitized diagnostic exports.

### 14.4 Maintainability

- Provider modules independently testable.
- Profiles data-driven where possible.
- Public contracts documented.
- Architecture dependency tests.
- Code coverage used as a diagnostic, not a sole quality target.
- ADRs for significant decisions.

### 14.5 Portability

- Windows development supported.
- Linux containers supported.
- WSL development documented.
- No CUDA-only assumptions in core code.
- Hardware telemetry is optional and adapter-based.

---

## 15. Observability model

### 15.1 Timeline events

A completion timeline may include:

- request accepted;
- request body read;
- validation complete;
- model/runtime resolved;
- structural summary created;
- upstream connection started;
- upstream headers received;
- first upstream byte;
- first decoded SSE event;
- first semantic output event;
- first client event flushed;
- native tool call observed;
- adapter invoked/applied/rejected/skipped;
- upstream completed;
- client completed/cancelled;
- timeout fired with phase;
- metadata queued and persisted/failed.

Unknown events remain unknown. Runtime prefill completion is recorded only when a reliable signal exists.

### 15.2 Core latency measurements

- gateway queue time;
- parsing/validation time;
- routing time;
- upstream connection time;
- time to response headers;
- time to first upstream byte;
- time to first semantic event;
- time to first client event;
- prompt-processing duration where observable;
- generation duration where observable;
- adapter buffering delay;
- persistence delay;
- total wall-clock duration.

### 15.3 Token and throughput measurements

Token values may come from client estimates, gateway tokenizer estimates, upstream usage, runtime-log parsers, or known fixtures. Source is mandatory. Prompt tokens/s and generation tokens/s are separate metrics. A calculation with insufficient evidence is unknown, not zero.

### 15.4 OpenTelemetry names

Proposed meter: `AgentSplice`.

Proposed traces:

```text
agentsplice.exchange
agentsplice.provider.request
agentsplice.stream
agentsplice.persistence
agentsplice.replay
agentsplice.conformance.case
agentsplice.evaluation.run
agentsplice.adapter
```

Metric and attribute conventions are normative in `docs/OBSERVABILITY.md`.

## 16. Security and privacy requirements

### 16.1 Data classes

- Public configuration.
- Operational metadata.
- Source-code content.
- Prompt and response content.
- Tool arguments.
- Credentials and tokens.
- Personal or regulated data.
- Diagnostic exports.

### 16.2 Default retention

- Prompts and responses: not stored.
- Headers: allowlisted metadata only.
- Metrics: aggregated and bounded labels.
- Request metadata: configurable short retention.
- Replay artifacts: opt-in with explicit expiration.
- Credentials: never copied into exchanges.

### 16.3 Threats

Key threats include:

- exposed gateway allowing unauthorized inference;
- SSRF through arbitrary runtime URL configuration;
- prompt or source-code leakage through logs;
- replay artifacts containing secrets;
- false-positive tool normalization;
- denial of service through oversized prompts or never-ending streams;
- decompression or JSON nesting abuse;
- malicious upstream SSE framing;
- administrative profile tampering;
- path traversal in support-pack loading;
- dashboard cross-site scripting from model content;
- credential forwarding to the wrong runtime.

Detailed mitigations are in `docs/THREAT_MODEL.md`.

---

## 17. Testing strategy

### 17.1 Unit tests

Cover parsers, normalizers, model resolution, policies, sanitization, metric calculations, timeout classification, and configuration validation.

### 17.2 Contract tests

Validate public endpoint behavior against captured fixtures and generated cases. Contract tests must include streaming framing and unknown-property behavior.

### 17.3 Provider integration tests

Use a controllable fake upstream server capable of:

- delaying headers;
- streaming arbitrary fragments;
- sending malformed JSON;
- returning structured or text tool calls;
- closing connections;
- reporting usage;
- simulating provider errors.

Optional local tests may run against an actual LM Studio instance but must not be required in CI.

### 17.4 Architecture tests

Enforce project dependency directions, no controller-to-infrastructure shortcuts, and naming conventions for provider and protocol modules.

### 17.5 Property and fuzz tests

Apply to SSE parser, JSON-fragment assembler, XML tool-call parser, sanitizers, and schema compaction.

### 17.6 Golden fixtures

Store sanitized protocol fixtures under `tests/fixtures`. Each fixture includes:

- source;
- expected parse;
- expected normalization;
- profile version;
- provenance notes;
- license or synthetic status.

### 17.7 Performance tests

Measure gateway-only overhead using a fake upstream and full-system latency using a real runtime. Never mix the two in one reported number.

---

## 18. Benchmark and evaluation system

### 18.1 Evaluation layers

1. Protocol conformance.
2. Streaming integrity.
3. Tool-call conformance.
4. Controlled performance benchmarks.
5. Cache-evidence experiments.
6. Model/runtime/backend comparison.
7. Complete agent-loop evaluation.
8. Coding-task correctness and safety.
9. Version-to-version regression analysis.

### 18.2 Initial protocol scenarios

- `simple_text_001`;
- `long_prefill_001`;
- `long_generation_001`;
- `stream_split_bytes_001`;
- `stream_multiline_001`;
- `stream_cancel_001`;
- `tool_native_single_001`;
- `tool_native_multiple_001`;
- `tool_stream_fragments_001`;
- `tool_false_positive_001`;
- `cache_second_turn_001`;
- `runtime_malformed_stream_001`.

### 18.3 Agentic coding scenarios

Use synthetic or open-source repositories in disposable containers. Each scenario defines an initial hash, instruction, allowed tools, forbidden paths, network policy, time/turn limits, build/test commands, hidden assertions, and expected outcome. Never use confidential employer material.

Initial categories should include ASP.NET Core endpoint repair, EF Core migration, PostgreSQL query correction, xUnit coverage, Docker Compose change, constrained refactor, and structured-log diagnosis.

### 18.4 Result interpretation

Never rank an agent/model/runtime stack by a single number. At minimum report:

- correctness/conformance status;
- task success;
- build/tests;
- valid tool-call rate;
- unauthorized changes;
- turns/iterations;
- TTFT and latency phases;
- prompt and generation throughput;
- total task time;
- failure rate;
- memory observations;
- environment fingerprint;
- unsupported conditions.

### 18.5 Regression policy

Correctness regressions may block baseline promotion even when throughput improves. Shared CI uses deterministic fake-runtime performance gates. Hardware-dependent results run in controlled labeled environments and preserve raw iterations.

## 19. Dashboard design

The dashboard begins after the Stage 1 trace APIs are stable. It is a first-class diagnostic surface, but it does not control the core request path or query the database directly.

### 19.1 Screens

- Overview.
- Runtimes.
- Models and aliases.
- Exchanges.
- Exchange detail and latency waterfall.
- Exchange timeline.
- Observations and adapter manifests.
- Replay.
- Benchmarks.
- Compatibility matrix.
- Settings and retention.

### 19.2 UX principles

- Separate prompt processing from generation visually.
- Show wall-clock time prominently.
- Label estimated values.
- Display cold versus warm runs.
- Avoid default content exposure.
- Link every transformation to a rule and profile version.
- Make export bundles easy to generate.

### 19.3 Frontend stack

Suggested:

- React
- TypeScript
- Vite
- MUI or another restrained component library
- TanStack Query
- lightweight charts

The frontend must consume documented APIs; it should not directly query the database.

---

## 20. Deployment

### 20.1 Local process

```text
Agent client -> localhost AgentSplice -> localhost LM Studio
```

### 20.2 Docker Compose

```text
Agent client
   -> AgentSplice container
   -> host.docker.internal:1234 LM Studio
   -> PostgreSQL container (optional)
   -> OpenTelemetry Collector (optional)
```

### 20.3 Network defaults

- Bind to loopback by default.
- Docker example exposes only the gateway port.
- Database is not exposed publicly by default.
- Runtime endpoints are operator-configured.

### 20.4 Future deployment

Kubernetes is not a Stage 1 requirement. When added, account for:

- long-lived streaming connections;
- pod termination drain;
- sticky runtime affinity only if cache semantics require it;
- provider health;
- bounded replay workers;
- secrets integration.

---

## 21. Delivery stages

### Stage 0 — Foundation

- solution, boundaries, CI, fake upstream, OpenAPI, Docker skeleton, ADRs, threat model.

### Stage 1A — Transparent LM Studio proxy

- model discovery;
- non-streaming chat;
- aliases/routing;
- stable errors;
- structural exchange capture;
- contract tests.

### Stage 1B — Streaming and timeline

- incremental SSE;
- cancellation/disconnect;
- timeout phases;
- first-byte/first-event measurements;
- malformed-stream fixtures;
- bounded overhead.

### Stage 1C — Persistence and minimal dashboard

- SQLite metadata;
- PostgreSQL contracts;
- exchange/timeline APIs;
- Overview, Exchanges, Exchange Detail, and Runtimes screens;
- retention and privacy controls.

### Stage 1D — Local alpha

- Docker/direct setup;
- loopback defaults;
- real client/runtime trace;
- release workflow;
- demo and documentation.

### Stage 2A — Replay artifacts

- sanitization, integrity, exact/adapted replay, target controls.

### Stage 2B — Differential comparison

- multi-target replay and structural/stream/tool/timing diffs.

### Stage 2C — Protocol and SSE conformance

- OpenAI Chat Completions, errors, usage, finish reasons, cancellation, SSE.

### Stage 2D — Native tool conformance

- IDs, ordering, argument JSON/Schema, streaming fragments, tool results.

### Stage 2E — Cache evidence and compatibility reports

- repeated-prefix experiments, runtime-log adapters, confidence labels, matrix export.

### Stage 3A — Evaluation scenario model

- immutable tasks, assertions, environment snapshots.

### Stage 3B — Sandboxed coding-task runner

- disposable repositories, approved commands, file/network policy.

### Stage 3C — Agent/client comparison

- common tasks across OpenCode, Cline, Aider, or custom harnesses.

### Stage 3D — Regression history and CI

- baselines, relative thresholds, scheduled hardware results.

### Stage 4A — Anthropic protocol adapter

- Messages API and explicit translation-loss reports.

### Stage 4B — Optional tool-call recovery

- generic content JSON, Laguna XML, selected Qwen rules, strict fixtures.

### Stage 4C — Prompt/schema compaction

- deterministic opt-in policies and semantic-equivalence evidence.

### Stage 4D — Support packs

- profiles, adapters, fixtures, limitations, conformance, benchmarks, upstream status.

### Stage 5A — Client integrations

- OpenCode/Cline helpers, trace links, upstream patches.

### Stage 5B — Backend laboratory

- ROCm/Vulkan/CUDA/CPU evidence and task-level comparison.

### Stage 5C — Upstream program

- issue bundles, minimal reproductions, accepted contribution index, adapter retirement.

### Stage 5D — Community adapter SDK

- versioned contracts, validation CLI, security and distribution process.

## 22. Acceptance criteria for first public alpha

The first public alpha may be tagged when:

1. LM Studio model discovery works through a documented configuration.
2. Non-streaming and streaming completions work transparently.
3. Client cancellation and disconnect propagate.
4. SSE split-boundary and malformed-stream fixtures pass.
5. Unknown fields follow documented transparent/strict behavior.
6. Exchange and trace IDs are stable and correlated.
7. Timeline distinguishes upstream headers, first byte, first semantic event when observable, first client event, and completion.
8. Metrics preserve provenance and distinguish prompt from generation throughput.
9. SQLite metadata persistence works and failure is observable.
10. Raw content storage is disabled by default.
11. Default logs contain no prompt/response/tool content.
12. Minimal dashboard displays Overview, Exchanges, Exchange Detail, and Runtimes.
13. Docker and direct-process local setup are documented.
14. Loopback-only defaults are enforced.
15. Windows and Linux CI pass.
16. Security policy, threat model, and ADR 0006 are published.
17. At least one real client/runtime trace report is published.
18. The release makes no claim of vendor-specific tool-call recovery.

## 23. Risks and mitigations

### Risk: becoming irrelevant after upstream fixes

Mitigation: durable core is trace, replay, conformance, evaluation, regression, and portable evidence. Workarounds are optional adapters with retirement criteria.

### Risk: becoming a workaround collection

Mitigation: evidence-first policy, adapter lifecycle metadata, upstream issue/PR tracking, and version constraints.

### Risk: false tool-call conversion

Mitigation: text-to-tool recovery is deferred, profile-gated, schema-validated, disabled by default, and tested against adversarial prose.

### Risk: excessive scope

Mitigation: stages are outcome-based; Stage 1 prohibits replay, broad normalization, protocol translation, and evaluation infrastructure.

### Risk: protocol drift

Mitigation: isolated protocol modules, versioned fixtures, conformance reports, and compatibility declarations.

### Risk: sensitive code leakage

Mitigation: metadata-only default, sanitization before persistence/export, loopback defaults, header allowlists, and independent retention categories.

### Risk: misleading benchmarks

Mitigation: environment fingerprints, raw iterations, warm/cold distinction, provenance, unsupported-result retention, and separate correctness/performance rankings.

### Risk: replay causes unsafe tool execution

Mitigation: core replay treats tool calls as data and never executes them. Evaluation execution is isolated and separately permissioned.

### Risk: dependency on one runtime

Mitigation: provider abstraction, deterministic fake upstream, and later multi-runtime conformance.

### Risk: dashboard distorts backend design

Mitigation: documented administrative APIs; frontend never queries the database directly or controls the inference path.

### Risk: low adoption

Mitigation: one-command local setup, clear trace value before adapters, reproducible reports, neutral integrations, and upstream contributions.

## 24. Open questions

- Should profile files use YAML, JSON, or both?
- How much unknown-field preservation is possible without retaining raw request documents?
- Should tokenization adapters be optional packages because tokenizer dependencies can be large?
- What minimum confidence model is appropriate for cache classification?
- Should replay content encryption be implemented before any dashboard content view?
- Is an OpenAI Responses API adapter a Stage 2 or later requirement?
- Should MCP traffic ever be normalized by AgentSplice, or remain a separate project?
- Which tool-call formats warrant built-in support versus community support packs?
- How should profiles declare runtime-version constraints?
- Can an agent client consume transformation diagnostics without non-standard response fields?

Open questions must be resolved through ADRs rather than incidental code decisions.

---

## 25. Success metrics

### Product success

- number of captured real client/runtime combinations;
- percentage of exchanges with complete trustworthy latency boundaries;
- number of replay artifacts reproduced successfully;
- number of conformance suites/cases and verified combinations;
- number of detected regressions before promotion;
- number of agent evaluation scenarios with deterministic assertions;
- number of exportable issue bundles;
- number of accepted upstream contributions;
- number of adapters retired after upstream fixes.

### Engineering success

- stable streaming and cancellation tests;
- bounded memory under long streams;
- low measured gateway-only overhead;
- zero default content retention;
- no body leakage in default logs;
- provenance for every displayed metric;
- dependency-boundary compliance;
- reproducible releases and reports;
- core operation with all compatibility adapters disabled.

### Career/portfolio success

- demonstrates production-quality ASP.NET Core engineering;
- demonstrates LLM protocols, streaming, tools, and agent-loop understanding;
- demonstrates observability, replay, conformance, and evaluation design;
- demonstrates privacy and security judgment;
- demonstrates reproducible performance analysis;
- demonstrates open-source collaboration through accepted issues/PRs;
- demonstrates AMD/ROCm and local-inference expertise without unsupported low-level claims.

## 26. Glossary summary

- **Agent client:** software that orchestrates model conversations and tool execution.
- **Runtime:** process serving model inference.
- **Provider adapter:** AgentSplice component communicating with a runtime API.
- **Protocol adapter:** component representing a client-facing or upstream API schema.
- **Profile:** versioned rules for one model/runtime compatibility combination.
- **Normalization:** deterministic conversion between semantically equivalent representations.
- **TTFT:** time to first semantic output token.
- **Prefill:** processing input tokens before generation.
- **Decode/generation:** sequential output-token production.
- **Prefix cache:** reuse of computation for an identical prompt prefix.
- **MTP/speculative decoding:** generation acceleration using draft predictions verified by the target model.
- **Replay:** rerunning a sanitized recorded request.
- **Support pack:** profiles, fixtures, documentation, and benchmarks for a model family.

---

## 27. Final product boundary

AgentSplice succeeds when a developer can answer, with evidence:

- What did the client send structurally?
- What did AgentSplice forward?
- What did the runtime return?
- Where was total wall-clock time spent?
- Which measurements are exact, reported, estimated, inferred, or unknown?
- Was streaming protocol-valid?
- Was a tool call natively structured, merely printed, or adapted?
- Was prefix reuse probable, absent, or unknown?
- Can the interaction be replayed safely?
- How does it differ across targets or versions?
- Does the stack pass the relevant conformance suites?
- Did the agent complete the actual task and pass deterministic assertions?
- Which component most likely caused the failure?
- Can the evidence be exported for an upstream issue or pull request?

AgentSplice must still answer those questions when all currently known OpenCode, LM Studio, Qwen, Laguna, llama.cpp, and ROCm defects are fixed. That is the architectural and strategic boundary for every implementation decision.
