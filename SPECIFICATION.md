# AgentSplice — Complete Product and Engineering Specification

**Document status:** Foundational specification  
**Target readers:** maintainers, contributors, coding agents, reviewers, future employers, integration authors  
**Primary implementation language:** C# / .NET 8  
**Product form:** local-first modular monolith, extensible by provider and protocol adapters

---

## 1. Executive summary

AgentSplice is a protocol-normalizing gateway designed specifically for AI agents and coding assistants that communicate with local or self-hosted language-model runtimes. It provides a stable API surface to clients while absorbing inconsistencies in model identifiers, runtime capabilities, prompt templates, tool-call encodings, streaming formats, usage metadata, caching behavior, and hardware-dependent inference characteristics.

The project is motivated by a recurring systems problem: an agent workflow is a chain of components, and failures are frequently attributed to the model even when the actual defect is in a client schema, provider adapter, runtime parser, prompt template, sampler implementation, cache mechanism, or hardware backend. AgentSplice makes those boundaries explicit.

The first version is intentionally narrow. It exposes OpenAI-compatible model discovery and chat-completion endpoints, proxies requests to LM Studio, preserves streaming, records end-to-end metrics, applies profile-driven tool-call normalization, and verifies behavior with contract tests. Later stages add Anthropic compatibility, prompt and tool-schema compaction, cache diagnostics, a dashboard, request replay, automated benchmarks, client integrations, runtime-specific profiles, and upstream contributions.

The project is expected to serve three purposes simultaneously:

1. A usable local infrastructure component.
2. A rigorous engineering portfolio demonstrating applied-AI systems work.
3. A reproducible laboratory for finding and fixing compatibility and performance defects across the open-source inference ecosystem.

---

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

### 2.3 Why existing generic gateways are insufficient

General LLM gateways typically focus on provider routing, API keys, rate limits, cost accounting, fallback, and cloud-provider normalization. AgentSplice focuses on the less mature boundary between agent clients and local/self-hosted inference:

- tool-call recovery and validation;
- prompt and schema overhead;
- profile-driven runtime quirks;
- streaming tool arguments;
- prefix-cache diagnostics;
- MTP/speculative-decoding observations;
- local GPU telemetry;
- reproducible compatibility matrices;
- model-template-specific transformations;
- upstream-quality bug reproductions.

AgentSplice may eventually interoperate with general gateways, but it should not duplicate all of their enterprise routing features in the initial roadmap.

---

## 3. Product vision

### 3.1 Vision statement

Make local and self-hosted AI agents behave as predictable software systems rather than fragile combinations of partially compatible components.

### 3.2 Mission

Provide a transparent, testable, vendor-neutral compatibility layer that:

- normalizes protocols without hiding transformations;
- identifies where latency and correctness failures occur;
- enables agent clients to use more open models and runtimes;
- gives maintainers reproducible evidence for upstream fixes;
- supports commodity and non-CUDA hardware as first-class environments.

### 3.3 Product principles

#### P-001 — Transparent transformation

Every material modification to a request or response must be attributable to a named rule, model profile, provider adapter, or policy. The gateway must expose transformation metadata to traces and, where configured, response headers or diagnostic APIs.

#### P-002 — Compatibility before convenience

A narrow endpoint with complete and tested semantics is preferable to many endpoints that only superficially match a protocol.

#### P-003 — Local first

The default deployment must work on a developer workstation with Docker Compose or a direct .NET process and SQLite. Networked and PostgreSQL deployments must remain possible without changing domain behavior.

#### P-004 — Safe by default

Raw prompts, responses, credentials, and tool arguments are sensitive. Persistence is minimized and disabled by default for content bodies.

#### P-005 — Runtime neutrality

LM Studio is the first provider, not a permanent architectural dependency. Provider-specific behavior belongs in adapters.

#### P-006 — Client neutrality

OpenCode and Cline are target integrations, not core dependencies. The gateway communicates through documented protocols.

#### P-007 — Measurement over anecdote

Performance recommendations must be based on repeatable workloads, fixed configurations, and separate metrics for prompt processing, time to first token, generation, total latency, and tool-loop completion.

#### P-008 — Upstream where appropriate

When investigation proves that a defect belongs in OpenCode, Cline, llama.cpp, LM Studio, a model template, or another dependency, AgentSplice should produce a minimal reproduction and upstream contribution rather than permanently accumulating a workaround without review.

---

## 4. Goals and non-goals

### 4.1 Stage 1 goals

- Expose `GET /v1/models`.
- Expose `POST /v1/chat/completions`.
- Support non-streaming OpenAI-compatible chat completions.
- Support streaming chat completions over SSE.
- Proxy to LM Studio's OpenAI-compatible API.
- Preserve cancellation and client disconnect semantics.
- Maintain a catalog of configured runtime endpoints.
- Provide model aliases and capability profiles.
- Detect and normalize supported text-encoded tool calls.
- Preserve already structured tool calls.
- Validate tool names and arguments against the tools supplied by the client.
- Collect request, provider, streaming, token, transformation, and error metrics.
- Emit OpenTelemetry traces, metrics, and structured logs.
- Support SQLite locally and PostgreSQL through the same persistence contracts.
- Provide unit, integration, contract, and architecture tests.
- Ship a Docker image and Docker Compose development environment.

### 4.2 Stage 2 goals

- Add Anthropic-compatible Messages API support.
- Translate between supported OpenAI and Anthropic concepts where semantics are clear.
- Introduce bounded prompt and tool-schema compaction.
- Diagnose prefix-cache and checkpoint reuse behavior.
- Add a web dashboard for operations and performance.
- Store sanitized request/response envelopes for replay.
- Replay requests against alternate profiles or runtimes.
- Define and execute automated benchmark scenarios.
- Produce compatibility reports and regression comparisons.

### 4.3 Stage 3 goals

- Build integrations or plugins for OpenCode and Cline.
- Publish model profiles for Laguna, Qwen, and other selected open models.
- Compare ROCm, Vulkan, CUDA, CPU, and other available backends under reproducible conditions.
- Prepare and submit upstream pull requests.
- Provide exportable compatibility bundles suitable for issue reports.
- Add a profile SDK and community-contribution workflow.

### 4.4 Explicit non-goals

- Hosting or downloading model weights.
- Training foundation models.
- Executing shell, filesystem, browser, or MCP tools inside the core gateway.
- Becoming an IDE.
- Replacing OpenCode, Cline, Aider, or other agent clients.
- Implementing an unrestricted prompt-rewriting system.
- Providing semantic memory or RAG in Stage 1.
- Managing cloud billing across every commercial provider.
- Providing multi-tenant SaaS controls in the first releases.
- Guaranteeing that arbitrary text can be converted into a safe tool call.
- Hiding defects through silent fallback.

---

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

AgentSplice begins as a modular monolith with independently testable modules.

```text
Client
  |
  v
Ingress protocol adapter
  - OpenAI Chat Completions
  - Anthropic Messages (Stage 2)
  |
  v
Request pipeline
  - authentication (optional local token)
  - validation
  - correlation
  - model resolution
  - capability/profile resolution
  - prompt/schema policies
  |
  v
Provider abstraction
  - LM Studio
  - llama.cpp direct
  - Ollama
  - vLLM/SGLang (later)
  |
  v
Upstream model runtime
  |
  v
Response pipeline
  - stream decoding
  - tool-call normalization
  - usage normalization
  - transformation events
  - persistence/metrics
  |
  v
Client-compatible response
```

### 6.1 Modules

#### API module

Owns HTTP hosting, endpoint routing, authentication integration, request limits, exception mapping, health endpoints, and response headers.

#### Application module

Owns use cases such as list models, create completion, replay request, execute benchmark, resolve profile, and retrieve metrics.

#### Domain module

Owns provider-neutral concepts:

- RuntimeEndpoint
- ModelDescriptor
- ModelProfile
- CapabilitySet
- CompletionExchange
- TransformationEvent
- ToolDefinition
- NormalizationOutcome
- BenchmarkScenario
- BenchmarkRun
- ReplayArtifact

#### Protocol modules

Own external DTOs, serialization details, validation, and event mapping for OpenAI and Anthropic protocols.

#### Provider modules

Own upstream communication and provider-specific behavior. Stage 1 implements LM Studio.

#### Normalization module

Owns deterministic request and response transformations, including tool-call recovery.

#### Observability module

Owns ActivitySource, Meter, semantic conventions, event timing, and diagnostic enrichment.

#### Persistence module

Owns metadata storage, configuration snapshots, benchmark results, and optional sanitized replay artifacts.

---

## 7. Functional requirements

Requirement identifiers are stable references for issues, commits, tests, and pull requests.

### 7.1 Model discovery

**FR-MOD-001** The gateway shall expose `GET /v1/models` using an OpenAI-compatible response shape.

**FR-MOD-002** The response shall combine configured aliases with models discovered from enabled upstream runtimes.

**FR-MOD-003** Model discovery shall have a configurable cache duration.

**FR-MOD-004** A stale-cache policy shall define whether the gateway returns the last known model list when an upstream is unavailable.

**FR-MOD-005** Duplicate upstream model identifiers shall be disambiguated internally by runtime endpoint ID.

**FR-MOD-006** Client-visible model aliases shall resolve deterministically to one runtime and upstream model identifier.

**FR-MOD-007** The system shall reject alias cycles and duplicate aliases during startup validation.

**FR-MOD-008** Model metadata shall record the source of every capability claim: configured, discovered, probed, inferred, or unknown.

**FR-MOD-009** Capability probing shall be disabled by default in Stage 1 because probes may be expensive or behaviorally unsafe.

**FR-MOD-010** Administrative diagnostics shall distinguish configured models from currently reachable models.

### 7.2 Chat completions

**FR-CHAT-001** The gateway shall expose `POST /v1/chat/completions`.

**FR-CHAT-002** The gateway shall accept non-streaming requests.

**FR-CHAT-003** The gateway shall accept streaming requests with `stream=true`.

**FR-CHAT-004** The gateway shall validate the requested model before opening the upstream request.

**FR-CHAT-005** Unknown request properties shall be preserved when forwarding if they do not conflict with a gateway transformation policy.

**FR-CHAT-006** Unsupported properties shall follow a configurable behavior: reject, drop with warning, or passthrough.

**FR-CHAT-007** The default unsupported-property behavior shall be passthrough for provider-neutral unknown fields and reject for fields known to create unsafe ambiguity.

**FR-CHAT-008** The gateway shall forward client cancellation to the upstream HTTP request.

**FR-CHAT-009** The gateway shall stop reading and writing the stream when the client disconnects.

**FR-CHAT-010** The gateway shall apply configured connect, response-header, idle-stream, and total-request timeouts separately.

**FR-CHAT-011** Timeout failures shall identify the timeout phase without exposing internal network details.

**FR-CHAT-012** The gateway shall return a stable request identifier.

**FR-CHAT-013** The gateway shall retain the upstream request identifier when available.

**FR-CHAT-014** The gateway shall not retry a streaming completion after response bytes have been sent to the client.

**FR-CHAT-015** Non-streaming retries shall be disabled by default and, when enabled, limited to explicitly retryable transport failures.

**FR-CHAT-016** Model-generated content shall never be interpreted as gateway configuration.

### 7.3 Streaming

**FR-STR-001** Streaming responses shall use valid `text/event-stream` semantics.

**FR-STR-002** The gateway shall preserve event order.

**FR-STR-003** The gateway shall flush output with bounded delay.

**FR-STR-004** The gateway shall tolerate upstream SSE events split across arbitrary network reads.

**FR-STR-005** The gateway shall support multiline SSE `data` fields.

**FR-STR-006** The gateway shall distinguish transport framing from JSON payload framing.

**FR-STR-007** Invalid upstream events shall be handled according to a profile policy: reject stream, pass raw event, or annotate diagnostics.

**FR-STR-008** Normalization requiring lookahead shall use a configured bounded buffer.

**FR-STR-009** Buffer limits shall be enforced by bytes and elapsed time.

**FR-STR-010** The gateway shall not accumulate the full response merely to calculate metrics.

**FR-STR-011** `[DONE]` shall be recognized only according to the active protocol adapter.

**FR-STR-012** Usage-only terminal chunks shall be preserved when clients request them.

**FR-STR-013** The gateway shall emit time-to-first-upstream-byte and time-to-first-client-token measurements.

**FR-STR-014** Stream termination reason shall distinguish normal completion, client cancellation, upstream cancellation, timeout, malformed event, and connection loss.

### 7.4 Tool-call normalization

**FR-TOOL-001** Already structured tool calls shall pass through without semantic rewriting.

**FR-TOOL-002** The gateway shall validate structured tool names against the tools supplied in the request when tools are present.

**FR-TOOL-003** Tool arguments shall remain JSON strings at the protocol boundary where required by the client protocol.

**FR-TOOL-004** Text-to-tool normalization shall be enabled only by a selected model profile.

**FR-TOOL-005** Every normalizer shall have a stable rule identifier and version.

**FR-TOOL-006** A normalizer shall return one of: no match, valid match, ambiguous match, malformed candidate, or rejected candidate.

**FR-TOOL-007** Ambiguous matches shall never be emitted as executable tool calls by default.

**FR-TOOL-008** The gateway shall validate recovered tool names against the supplied tool set.

**FR-TOOL-009** The gateway shall validate recovered argument JSON syntax.

**FR-TOOL-010** Optional JSON Schema validation of arguments shall be supported.

**FR-TOOL-011** Schema validation failure shall not be automatically repaired unless an explicit repair policy is enabled.

**FR-TOOL-012** Normalizers shall reject ordinary prose that merely discusses tool-call syntax.

**FR-TOOL-013** Normalizers shall support Unicode tool arguments.

**FR-TOOL-014** Multiple tool calls shall preserve original order.

**FR-TOOL-015** The system shall support model-specific encodings through plugins or registered strategies.

**FR-TOOL-016** Stage 1 shall provide at least one generic JSON normalizer and one Laguna-style XML normalizer fixture, even if the latter remains disabled by default.

**FR-TOOL-017** Qwen-specific rules shall be profile-versioned rather than hard-coded globally.

**FR-TOOL-018** Streaming normalization shall assemble argument fragments without exposing invalid intermediate structured calls to the client.

**FR-TOOL-019** A bounded fallback may preserve raw text when normalization fails.

**FR-TOOL-020** A transformation event shall record source encoding, target encoding, rule, outcome, duration, and non-sensitive validation errors.

### 7.5 Model profiles

**FR-PROF-001** Profiles shall be addressable by stable profile ID and version.

**FR-PROF-002** Profiles shall match by explicit assignment first, then ordered rules.

**FR-PROF-003** Match inputs may include provider, model ID, architecture metadata, quantization metadata, and user-selected override.

**FR-PROF-004** Profiles shall declare supported ingress protocols.

**FR-PROF-005** Profiles shall declare expected tool-call encodings.

**FR-PROF-006** Profiles shall declare transformation failure policies.

**FR-PROF-007** Profiles shall declare unsupported parameters and recommended removals.

**FR-PROF-008** Profiles shall declare known runtime constraints as diagnostics, not silent configuration changes.

**FR-PROF-009** Profiles shall be loadable from version-controlled YAML or JSON files.

**FR-PROF-010** Invalid profiles shall fail startup when enabled.

**FR-PROF-011** Profile changes shall be auditable.

**FR-PROF-012** Hot reload may be added after Stage 1; initial implementation may require restart.

### 7.6 Metrics and traces

**FR-OBS-001** Every completion shall create a distributed trace span.

**FR-OBS-002** Child spans shall represent validation, model resolution, upstream connect, prompt processing when observable, streaming, normalization, and persistence.

**FR-OBS-003** Metrics shall include request count, active requests, errors, cancellations, upstream latency, total latency, time to first byte, time to first token, prompt tokens, completion tokens, prompt throughput, generation throughput, stream bytes, normalization outcomes, and model-discovery status.

**FR-OBS-004** Metric dimensions shall be bounded. Raw request IDs, prompts, and arbitrary model strings shall not become metric labels.

**FR-OBS-005** Logs shall be structured and correlated with trace and request IDs.

**FR-OBS-006** Request and response bodies shall not be logged by default.

**FR-OBS-007** Hardware metadata collection shall be optional.

**FR-OBS-008** Runtime log parsing shall be isolated from protocol proxying and disabled by default.

**FR-OBS-009** Estimated token values shall be explicitly labeled as estimates.

**FR-OBS-010** Upstream-reported usage shall remain distinguishable from gateway-calculated usage.

### 7.7 Persistence

**FR-DATA-001** Stage 1 shall run without a database for purely ephemeral proxy use.

**FR-DATA-002** SQLite shall be the default development database when persistence is enabled.

**FR-DATA-003** PostgreSQL shall be supported for shared or durable deployments.

**FR-DATA-004** Persistence shall store metadata independently from optional content bodies.

**FR-DATA-005** Raw request and response content storage shall be disabled by default.

**FR-DATA-006** Sanitization shall occur before content persistence.

**FR-DATA-007** Retention shall be configurable by artifact category.

**FR-DATA-008** Deletion jobs shall be idempotent.

**FR-DATA-009** Schema migrations shall be versioned.

**FR-DATA-010** Sensitive configuration values shall not be stored in replay artifacts.

### 7.8 Health and diagnostics

**FR-HEALTH-001** Expose liveness and readiness endpoints.

**FR-HEALTH-002** Liveness shall not depend on upstream availability.

**FR-HEALTH-003** Readiness shall optionally require at least one enabled runtime.

**FR-HEALTH-004** Runtime health shall distinguish unreachable, authentication failed, incompatible response, and no models available.

**FR-HEALTH-005** A diagnostic endpoint shall expose build version, enabled modules, protocol versions, and redacted configuration summary.

**FR-HEALTH-006** Administrative diagnostics shall require authentication when bound beyond loopback.

---

## 8. Stage 2 functional requirements

### 8.1 Anthropic-compatible Messages API

**FR-ANTH-001** Expose an Anthropic-compatible messages endpoint for supported semantics.

**FR-ANTH-002** Preserve system content, user/assistant messages, tool definitions, tool-use blocks, and tool-result blocks where mapping is lossless.

**FR-ANTH-003** Reject translations that would materially change ordering or role semantics.

**FR-ANTH-004** Streaming content-block events shall maintain valid lifecycle ordering.

**FR-ANTH-005** Protocol translation shall record a transformation report.

**FR-ANTH-006** Vendor-specific beta headers shall be handled through explicit compatibility policies.

### 8.2 Prompt and schema compaction

**FR-COMP-001** Compaction shall be deterministic for a fixed input and policy version.

**FR-COMP-002** Compaction shall never remove required JSON Schema constraints silently.

**FR-COMP-003** The system shall support description trimming, example removal, whitespace normalization, duplicate-definition elimination, and selected-schema projection.

**FR-COMP-004** Semantic tool selection may be explored later but shall not be part of the first compaction implementation.

**FR-COMP-005** Original and compacted token estimates shall be recorded.

**FR-COMP-006** Compaction rules shall be individually configurable.

**FR-COMP-007** The client may opt out per request where supported.

**FR-COMP-008** Contract tests shall prove that compacted schemas still validate the same accepted and rejected fixtures for covered transformations.

### 8.3 Cache diagnostics

**FR-CACHE-001** The gateway shall distinguish gateway response caching from model prefix/KV cache behavior.

**FR-CACHE-002** AgentSplice shall not cache generated model responses by default.

**FR-CACHE-003** Cache diagnostics shall infer reuse only from observable evidence and label confidence.

**FR-CACHE-004** Evidence may include upstream timings, token counts, slot metadata, provider headers, and runtime log events.

**FR-CACHE-005** The system shall classify a request as cold, probable prefix hit, partial prefix hit, probable miss, or unknown.

**FR-CACHE-006** Cache classifications shall not alter the response path.

**FR-CACHE-007** Diagnostic reports shall state that cache behavior may depend on model architecture, runtime, prompt identity, slot scheduling, and backend.

### 8.4 Replay

**FR-REPLAY-001** Replay shall use sanitized immutable artifacts.

**FR-REPLAY-002** Replays shall never include original credentials.

**FR-REPLAY-003** A replay may target the original or another model profile.

**FR-REPLAY-004** The system shall distinguish exact replay from adapted replay.

**FR-REPLAY-005** Adapted replay shall record every changed field.

**FR-REPLAY-006** Replays shall not execute client-side tools.

**FR-REPLAY-007** Replays shall have concurrency and rate limits.

**FR-REPLAY-008** Results shall support structural, textual, tool-call, latency, and usage comparisons.

### 8.5 Dashboard

**FR-DASH-001** The dashboard shall be optional and separately deployable.

**FR-DASH-002** It shall display runtime health, model catalog, request volume, errors, TTFT, prompt throughput, generation throughput, total latency, normalization outcomes, cache classifications, and benchmark results.

**FR-DASH-003** It shall avoid displaying prompt content unless the operator explicitly enables and is authorized to view stored content.

**FR-DASH-004** It shall support filtering by bounded identifiers such as runtime, profile, status class, and benchmark scenario.

**FR-DASH-005** It shall expose transformation timelines for individual retained exchanges.

### 8.6 Benchmarks

**FR-BENCH-001** Benchmarks shall define immutable scenario versions.

**FR-BENCH-002** A scenario shall define prompt, tools, expected protocol behavior, timeout, warm-up policy, repetition count, and validation rules.

**FR-BENCH-003** Benchmarks shall separate correctness and performance scores.

**FR-BENCH-004** The runner shall record software, model, quantization, runtime, profile, context, and available hardware metadata.

**FR-BENCH-005** Results shall distinguish warm and cold runs.

**FR-BENCH-006** Benchmark execution shall not share mutable conversation state unless the scenario explicitly tests it.

**FR-BENCH-007** Coding benchmarks may run in disposable containers in a later increment.

**FR-BENCH-008** The first benchmark set shall focus on protocol and tool-call correctness rather than general intelligence claims.

---

## 9. Stage 3 functional requirements

### 9.1 Client integrations

**FR-INT-001** OpenCode integration shall support automatic provider configuration and model discovery.

**FR-INT-002** OpenCode integration shall optionally disable or redirect title-generation requests.

**FR-INT-003** OpenCode integration shall surface AgentSplice diagnostic identifiers.

**FR-INT-004** Cline integration shall provide a documented local-provider setup and compact-profile recommendations.

**FR-INT-005** Integrations shall not require a fork when an extension or configuration path exists.

**FR-INT-006** Any fork used for research shall have an upstreaming plan.

### 9.2 Model support packs

**FR-PACK-001** A support pack shall contain profiles, fixtures, known limitations, recommended client settings, and benchmark baselines.

**FR-PACK-002** Laguna support shall include its observed XML-style tool encoding and streaming variants.

**FR-PACK-003** Qwen support shall distinguish model family, MTP versus non-MTP checkpoints, tool template, and runtime compatibility.

**FR-PACK-004** Packs shall identify evidence sources and test dates.

**FR-PACK-005** Community packs shall undergo schema validation and automated fixtures.

### 9.3 Backend comparison

**FR-BACK-001** Comparison reports shall use the same model file where possible.

**FR-BACK-002** Reports shall document when a comparison uses different kernels or quantization implementations.

**FR-BACK-003** ROCm, Vulkan, CUDA, CPU, and other backends shall be treated as distinct execution environments.

**FR-BACK-004** Reports shall include prompt processing, generation, TTFT, total task time, stability, memory, tool correctness, and unsupported operations.

**FR-BACK-005** Unsupported configurations shall be reported, not omitted.

---

## 10. API behavior

### 10.1 Base URL and versioning

Stage 1 uses OpenAI-compatible paths under `/v1`. Gateway-specific administrative APIs use `/api/v1` to avoid pretending that non-standard endpoints are part of the OpenAI contract.

```text
/v1/models
/v1/chat/completions
/api/v1/health/runtimes
/api/v1/profiles
/api/v1/exchanges/{id}
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
- profile configuration;
- upstream connection;
- upstream authentication;
- upstream timeout;
- upstream status;
- upstream protocol;
- normalization;
- persistence;
- cancellation;
- internal defect.

### 10.4 Streaming transformation strategy

A streaming response passes through these logical phases:

1. Read and parse SSE frame.
2. Decode protocol payload.
3. Add event to response-state machine.
4. Apply profile-specific streaming normalizer.
5. Emit zero, one, or multiple client events.
6. Record non-content metrics.
7. Flush according to bounded policy.

Tool-call normalizers may need to delay a small portion of output while determining whether a sequence is a tool invocation. Such buffering must be explicit, bounded, measured, and visible in diagnostics.

---

## 11. Tool-call normalization design

### 11.1 Normalization pipeline

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

### 11.2 Candidate types

- Native structured tool calls.
- OpenAI-like JSON inside content.
- XML-tagged tool calls.
- Model-family-specific delimiters.
- Mixed prose and tool candidate.
- Partial streaming candidate.

### 11.3 Safety rules

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

### 11.4 Failure policies

- `passthrough`: preserve content and record failure.
- `reject`: return a normalization error.
- `annotate`: preserve content and add diagnostic metadata where protocol allows.
- `strip-candidate`: prohibited as a default because it may destroy meaningful text.

### 11.5 Normalizer interfaces

Conceptual C# contracts:

```csharp
public interface IToolCallNormalizer
{
    string RuleId { get; }
    Version RuleVersion { get; }

    ValueTask<NormalizationOutcome> NormalizeAsync(
        NormalizationContext context,
        CancellationToken cancellationToken);
}
```

Streaming normalizers require a state object scoped to one response and must never use global mutable state.

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
  persistence:
    mode: sqlite
    connectionString: Data Source=/data/agentsplice.db
  diagnostics:
    storeBodies: false
    storeHeaders: allowlist
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
      profileId: qwen36-mtp-lmstudio
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
- ProfileId
- Enabled
- Priority
- Metadata

### 13.3 ModelProfile

- ProfileId
- Version
- MatchRules
- SupportedIngressProtocols
- ProviderParameterPolicies
- ToolEncoding
- Normalizers
- FailurePolicies
- KnownLimitations
- EvidenceMetadata

### 13.4 CompletionExchange

- ExchangeId
- PublicRequestId
- TraceId
- StartedAt
- CompletedAt
- RuntimeEndpointId
- ClientModelId
- UpstreamModelId
- ProfileId and version
- Streaming
- Status
- FailureClass
- Timings
- Usage sources
- ContentRetentionState

### 13.5 TransformationEvent

- EventId
- ExchangeId
- Sequence
- RuleId and version
- Direction
- TransformationType
- Outcome
- Duration
- SafeDetails

### 13.6 ReplayArtifact

- ArtifactId
- SourceExchangeId
- CreatedAt
- SanitizerVersion
- RequestProtocol
- SanitizedRequest
- ContentClassification
- Expiration
- IntegrityHash

### 13.7 Benchmark entities

- Scenario
- ScenarioVersion
- Run
- Iteration
- EnvironmentSnapshot
- CorrectnessAssertion
- PerformanceMeasurement
- Comparison

---

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
- validation complete;
- model resolved;
- profile selected;
- upstream request started;
- upstream headers received;
- first SSE event received;
- first semantic token observed;
- first client event flushed;
- tool candidate detected;
- tool call normalized;
- upstream completed;
- client stream completed;
- metadata persisted.

### 15.2 Core latency measurements

- gateway queue time;
- validation time;
- upstream connection time;
- time to response headers;
- time to first upstream byte;
- time to first semantic token;
- time to first client token;
- prompt-processing duration where available;
- generation duration where available;
- normalization buffering delay;
- total wall-clock duration.

### 15.3 Token and throughput measurements

Token values may come from:

- client estimate;
- gateway tokenizer estimate;
- upstream response usage;
- runtime log parser;
- benchmark-known fixture.

The source must be retained. Calculated throughput without a reliable token count must not be presented as exact.

### 15.4 OpenTelemetry names

Proposed meter:

```text
AgentSplice.Gateway
```

Proposed traces:

```text
agentsplice.completion
agentsplice.provider.request
agentsplice.stream
agentsplice.normalization
agentsplice.persistence
```

Metric names should follow stable conventions documented in `docs/OBSERVABILITY.md`.

---

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

## 18. Benchmark system

### 18.1 Benchmark categories

1. Protocol passthrough.
2. Streaming integrity.
3. Tool-call correctness.
4. Prompt/schema overhead.
5. Cache behavior.
6. Model/runtime/hardware throughput.
7. Agent-loop completion.
8. Coding-task correctness.

### 18.2 Initial benchmark scenarios

- `simple_text_001`: short prompt, no tools.
- `long_prefill_001`: fixed 8K-token prompt, short answer.
- `long_generation_001`: short prompt, 1K-token answer.
- `tool_native_single_001`: native structured tool call.
- `tool_text_json_single_001`: JSON tool call in content.
- `tool_laguna_xml_single_001`: Laguna-style XML call.
- `tool_false_positive_001`: prose discussing tool syntax.
- `tool_stream_fragments_001`: arguments split across SSE events.
- `cache_second_turn_001`: repeated stable prefix with short suffix.
- `cancel_midstream_001`: client cancellation during generation.

### 18.3 Result interpretation

The dashboard and reports must avoid ranking models by a single number. At minimum report:

- correctness pass/fail;
- valid tool-call rate;
- false-positive normalization rate;
- TTFT;
- prompt throughput;
- generation throughput;
- total completion time;
- stability/failure rate;
- memory observations;
- environment fingerprint.

---

## 19. Dashboard design

The dashboard is secondary to the API and may begin in Stage 2.

### 19.1 Screens

- Overview.
- Runtimes.
- Models and aliases.
- Profiles.
- Exchanges.
- Exchange timeline.
- Transformations.
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

Deliverables:

- repository standards;
- solution skeleton;
- CI;
- configuration model;
- ADRs;
- fake upstream test server;
- OpenAPI draft;
- Docker development skeleton.

Exit criteria:

- build and tests execute on Windows and Linux;
- architecture boundaries are enforced;
- no production proxy behavior claimed.

### Stage 1A — Transparent LM Studio proxy

Deliverables:

- model discovery;
- non-streaming chat completion;
- stable errors;
- model aliases;
- cancellation;
- structured logging;
- contract tests.

Exit criteria:

- a standard client can list models and complete a non-streaming request through AgentSplice;
- unknown fields and errors follow documented rules.

### Stage 1B — Streaming

Deliverables:

- SSE parser and writer;
- streaming passthrough;
- disconnect propagation;
- timing metrics;
- malformed-stream tests.

Exit criteria:

- long streaming responses pass without full buffering;
- cancellation and disconnect tests are reliable.

### Stage 1C — Tool normalization

Deliverables:

- model profiles;
- native passthrough;
- generic content-JSON normalizer;
- Laguna XML normalizer;
- schema validation;
- transformation events;
- false-positive corpus.

Exit criteria:

- supported malformed/runtime-unparsed tool calls become valid client calls;
- ordinary prose remains prose;
- every transformation is traceable.

### Stage 1D — Persistence and packaging

Deliverables:

- SQLite and PostgreSQL support;
- metadata persistence;
- Docker image;
- Compose examples;
- retention jobs;
- release workflow.

Exit criteria:

- local install documented;
- no raw body storage by default;
- releases are reproducible.

### Stage 2A — Protocol expansion

- Anthropic Messages ingress and egress.
- Translation reports.
- Protocol conformance fixtures.

### Stage 2B — Compaction and cache diagnostics

- deterministic schema compaction;
- token-overhead reports;
- cache evidence classifier;
- repeated-prefix benchmark.

### Stage 2C — Replay and dashboard

- sanitized replay artifacts;
- exact/adapted replay;
- operations UI;
- exchange timeline.

### Stage 2D — Benchmark automation

- scenario format;
- runner;
- environment snapshot;
- comparison reports;
- CI-safe fake-runtime benchmark.

### Stage 3A — Client integrations

- OpenCode plugin/configuration helper;
- Cline provider guide or extension;
- diagnostic headers surfaced to clients.

### Stage 3B — Support packs

- Laguna pack;
- Qwen pack;
- versioned compatibility fixtures;
- community profile validation.

### Stage 3C — Backend lab

- ROCm comparison workflow;
- Vulkan comparison workflow where supported;
- CUDA community results;
- standardized result bundle;
- upstream issue templates.

### Stage 3D — Upstream contributions

- minimal reproductions;
- tests;
- documentation patches;
- targeted client/runtime fixes;
- accepted PR tracking.

---

## 22. Acceptance criteria for first public release

The first public release may be tagged when:

1. LM Studio model discovery works.
2. Non-streaming and streaming completions work.
3. Client cancellation propagates.
4. At least one text tool encoding is normalized safely.
5. Native tool calls pass unchanged.
6. False-positive fixtures pass.
7. Metrics distinguish TTFT, total time, and usage source.
8. Docker image and local process instructions exist.
9. Content persistence is off by default.
10. Security policy and threat model are published.
11. Windows and Linux CI pass.
12. A sample OpenCode or Cline configuration is documented.
13. At least one real compatibility report is published.

---

## 23. Risks and mitigations

### Risk: becoming a workaround collection

Mitigation: every workaround is a versioned profile rule with evidence, tests, owner, and upstream status.

### Risk: false tool-call conversion

Mitigation: strict profile matching, request-tool validation, schema validation, ambiguous-result rejection, adversarial fixtures, and disabled-by-default text normalizers.

### Risk: excessive scope

Mitigation: staged roadmap, modular monolith, Stage 1 scope contract in `CLAUDE.md`, and issue templates requiring requirement IDs.

### Risk: protocol drift

Mitigation: isolated protocol modules, captured fixtures, contract tests, and versioned compatibility notes.

### Risk: sensitive code leakage

Mitigation: no body storage by default, sanitization before persistence, local-only defaults, header allowlists, and export review.

### Risk: misleading benchmarks

Mitigation: environment fingerprints, fixed scenarios, repeated runs, warm/cold distinction, separate correctness/performance, and raw result export.

### Risk: dependency on one local runtime

Mitigation: provider abstraction from first implementation and fake-upstream contract tests.

### Risk: low adoption

Mitigation: solve concrete integration defects, publish reproducible support packs, provide one-command Docker setup, and upstream fixes.

---

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

- number of supported client/runtime/model combinations;
- percentage of benchmark tool calls represented correctly;
- reduction in invalid tool-call failures;
- reduction in prompt/tool-schema tokens under compaction;
- number of reproducible upstream issues;
- number of accepted upstream contributions;
- number of community profiles with passing fixtures.

### Engineering success

- stable streaming tests;
- bounded memory under concurrency;
- low gateway-only latency;
- zero default content retention;
- dependency-boundary compliance;
- reproducible releases;
- documented performance claims.

### Career/portfolio success

- demonstrates production-quality ASP.NET Core engineering;
- demonstrates LLM protocol and agent-tool understanding;
- demonstrates observability and performance analysis;
- demonstrates open-source collaboration through accepted PRs;
- demonstrates AMD/ROCm and local-inference expertise without claiming unsupported kernel expertise.

---

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

- What did the client send?
- What transformations were applied?
- What did the runtime receive?
- When did prompt processing end?
- When did generation start?
- Did the runtime expose or merely print a tool call?
- Was a tool call normalized, and under which rule?
- Was cache reuse probable?
- Which component caused the failure?
- Can the exact behavior be reproduced safely?

That boundary should guide every implementation decision.
