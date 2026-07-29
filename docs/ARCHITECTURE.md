# Architecture

## Architectural style

AgentSplice begins as a local-first modular monolith. The request path is latency-sensitive and benefits from in-process calls, while the codebase still needs hard boundaries between protocol handling, provider communication, trace capture, persistence, replay, conformance, evaluation, and optional adapters.

The architecture separates a **durable core** from **replaceable compatibility adapters**.

Durable core:

- transparent proxying;
- exchange/timeline capture;
- observability and provenance;
- sanitized replay;
- protocol conformance;
- agent evaluation;
- report/export infrastructure.

Replaceable adapters:

- specific client integrations;
- provider/runtime integrations;
- protocol translations;
- text-to-tool recovery;
- prompt/schema compaction;
- runtime-log parsers;
- model-family profiles;
- backend telemetry collectors.

## System context

```text
OpenCode / Cline / Aider / custom harness
                    |
                    | OpenAI or Anthropic-compatible traffic
                    v
+---------------------------------------------------------+
|                      AgentSplice                        |
|                                                         |
|  ingress protocol       trace/timeline                  |
|  transparent forwarding persistence/retention           |
|  replay/conformance     evaluation/reporting            |
|  optional adapters      OpenTelemetry export            |
+---------------------------------------------------------+
                    |
                    v
LM Studio / llama.cpp / Ollama / vLLM / SGLang / service
                    |
                    v
              model + backend
```

AgentSplice does not execute model-requested tools in the core gateway. Agent clients remain responsible for tool execution. A later isolated evaluation worker may execute explicitly approved commands inside disposable task environments.

## Proposed solution structure

```text
AgentSplice.sln
src/
  AgentSplice.Api
  AgentSplice.Application
  AgentSplice.Domain
  AgentSplice.Infrastructure
  AgentSplice.Protocols.OpenAI
  AgentSplice.Providers.LmStudio
  AgentSplice.Observability

  # Added in later stages
  AgentSplice.Replay
  AgentSplice.Conformance
  AgentSplice.Evaluation
  AgentSplice.Protocols.Anthropic
  AgentSplice.Adapters.ToolCalls
  AgentSplice.Adapters.PromptCompaction
  AgentSplice.Providers.LlamaCpp
  AgentSplice.Providers.Ollama

web/
  AgentSplice.Dashboard

tests/
  AgentSplice.TestSupport
  AgentSplice.UnitTests
  AgentSplice.ContractTests
  AgentSplice.IntegrationTests
  AgentSplice.ArchitectureTests
  AgentSplice.PerformanceTests
  AgentSplice.ConformanceTests
  AgentSplice.EvaluationTests
```

Projects may be consolidated if the initial repository becomes fragmented, but logical boundaries must remain explicit.

`AgentSplice.TestSupport` holds the deterministic fake upstream and shared fixtures. It is required by the contract, integration, and later performance and conformance suites, so it cannot live inside any one of them. It references no production project, so the fake upstream behaves like a third-party runtime rather than a mirror of the gateway's own types. See ADR 0007.

## Dependency rules

- Domain references no infrastructure project.
- Application references Domain.
- Protocol modules reference Application contracts and their own DTOs.
- Provider modules implement Application ports.
- Infrastructure implements persistence, retention, and hosting-adjacent ports.
- Observability instruments application events without owning business behavior.
- Replay depends on sanitized artifacts, not live HTTP controller types.
- Conformance depends on public protocol contracts and fixture abstractions.
- Evaluation depends on scenario/run abstractions and isolated execution ports.
- Compatibility adapters depend on explicit adapter contracts and must not be referenced by Domain.
- API composes modules and contains no transformation or evaluation logic.

## Stage 1 primary request flow

1. HTTP request enters an ingress protocol adapter.
2. Request limits, optional local authentication, and correlation run.
3. Protocol shape is validated.
4. Application resolves model alias and runtime.
5. A safe structural request observation is created.
6. Provider adapter creates the upstream request with no semantic rewriting.
7. Upstream headers and body/stream are received incrementally.
8. Protocol observations and timing boundaries are emitted.
9. Output is forwarded to the client.
10. Metadata persistence completes outside any long-lived request transaction.
11. OpenTelemetry spans and metrics are finalized with provenance.

Routing-only changes, such as mapping a client-visible model alias to an upstream model ID, must be represented as explicit events.

## Later-stage request flow with adapters

Optional adapters may run before or after provider communication:

```text
Ingress DTO
  -> canonical structural model
  -> optional request adapter chain
  -> provider request
  -> provider stream/events
  -> optional response adapter chain
  -> egress protocol
```

Every adapter invocation produces a manifest containing adapter ID/version, activation reason, outcome, safe details, duration, and failure policy. Adapters are disabled unless explicitly selected by configuration/profile.

## Streaming architecture

Use `HttpCompletionOption.ResponseHeadersRead`. Parse SSE incrementally and preserve semantic event order. Never assume network reads align with events or UTF-8 boundaries.

```csharp
public interface ICompletionGateway
{
    Task<CompletionResult> CompleteAsync(
        CanonicalCompletionRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<CompletionStreamEvent> StreamAsync(
        CanonicalCompletionRequest request,
        CancellationToken cancellationToken);
}

public interface IModelRuntimeProvider
{
    Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(
        CancellationToken cancellationToken);

    Task<ProviderCompletion> CompleteAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken);

    IAsyncEnumerable<ProviderStreamEvent> StreamAsync(
        ProviderCompletionRequest request,
        CancellationToken cancellationToken);
}
```

Stage 1 forwarding should not require full-event JSON rewriting when raw passthrough is safe. The implementation may parse enough structure to validate framing and capture safe metadata, but must avoid hidden semantic transformation.

## Canonical structural model

The canonical model includes only concepts AgentSplice can define consistently:

- model selector;
- ordered messages/content blocks;
- tool declarations;
- tool choice;
- generation options;
- streaming preference;
- extension bag;
- response content blocks;
- structured tool calls;
- finish reason;
- usage with provenance.

Unknown protocol/provider fields may remain in extension documents. The canonical model must never imply lossless equivalence between OpenAI and Anthropic semantics.

## Exchange and timeline model

An exchange is the primary durable observation unit. It contains:

- public request and trace identifiers;
- ingress protocol;
- client/runtime/model/profile identifiers;
- structural request and response summaries;
- timestamps and duration phases;
- stream termination state;
- usage values with provenance;
- transformation/adapter manifests;
- content-retention state;
- links to replay, conformance, and evaluation records.

Timeline events are immutable and sequence-ordered. Unknown timestamps remain absent rather than inferred.

## Persistence

Use EF Core only in Infrastructure. SQLite is the local default; PostgreSQL supports shared/self-hosted installations.

Streaming must remain independent of database transaction duration. Metadata may be queued through a bounded in-process channel. Queue saturation and persistence failure require explicit metrics and policies; they must not silently block model output indefinitely.

Raw content storage is disabled by default. Sanitization occurs before persistence or export.

## Replay architecture

Replay consumes an immutable sanitized artifact, not an arbitrary live database row. A replay worker resolves a target, applies an explicit adaptation manifest when required, invokes the same provider abstractions, and produces a new exchange linked to the source artifact.

Replay does not execute client tools. Exact and adapted replay are distinct domain concepts.

## Conformance architecture

Conformance suites are versioned collections of fixtures and assertions. They may target:

- AgentSplice ingress/egress behavior;
- a provider/runtime combination through AgentSplice;
- protocol translation;
- streaming;
- tool-call lifecycle;
- cache evidence;
- cancellation and timeout behavior.

Fast suites use the deterministic fake upstream in CI. Hardware suites require environment snapshots and produce scoped claims.

## Evaluation architecture

Agentic evaluations run outside the latency-sensitive gateway process. A later worker orchestrates disposable repositories/containers, approved tools, timeouts, assertions, and result capture.

The evaluation worker must be separately permissioned because it may execute commands. The core gateway continues to treat tool calls as data.

## Dashboard architecture

The React dashboard consumes documented `/api/v1` endpoints. It does not query the database directly. Initial screens are Overview, Exchanges, Exchange Detail, and Runtimes. Later screens add Replay, Conformance, Evaluations, Compatibility Matrix, Profiles/Adapters, and Settings.

## Background processing

Stage 1 background services:

- model discovery refresh;
- retention cleanup;
- bounded metadata persistence queue;
- runtime health refresh.

Later services:

- replay worker;
- conformance runner;
- evaluation worker;
- report generation;
- runtime-log ingestion;
- scheduled regression comparisons.

## Extensibility and adapter lifecycle

Extension points include:

- provider adapters;
- ingress/egress protocols;
- model/runtime profiles;
- request/response compatibility adapters;
- token estimators;
- hardware telemetry collectors;
- replay sanitizers;
- conformance validators;
- evaluation assertions;
- report exporters.

Dynamic plugin loading is deferred. Compile-time registration and versioned configuration are safer initially.

Every compatibility adapter requires activation constraints, fixtures, evidence, failure policy, upstream status, and retirement criteria. An upstream fix should result in adapter simplification or deprecation, not in preserving unnecessary behavior.
