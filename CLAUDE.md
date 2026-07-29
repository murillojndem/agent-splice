# CLAUDE.md — AgentSplice implementation contract

This file is the operating contract for Claude or any coding agent working in this repository. Read it before changing code.

## Product definition

AgentSplice is a local-first interoperability, observability, replay, conformance, and evaluation platform for AI-agent workloads.

It accepts supported client protocol requests, transparently forwards them to configured model runtimes, preserves streaming semantics, reconstructs a timing and protocol timeline, records privacy-safe evidence, and exposes that evidence for diagnosis, replay, conformance testing, and regression analysis.

Optional adapters may translate protocols, compact prompts/tool schemas, parse runtime logs, or recover known text-encoded tool calls. Those adapters are not the durable product core.

AgentSplice is not:

- an autonomous agent;
- a model runtime;
- an MCP execution host;
- a prompt marketplace;
- a vector database;
- a general cloud-provider billing router;
- a replacement for OpenCode, Cline, Aider, LM Studio, llama.cpp, or Ollama;
- a permanent collection of third-party workarounds.

## Strategic invariant

The implementation must remain useful if OpenCode and LM Studio become perfectly compatible. Trace capture, replay, conformance, differential comparison, evaluation, and OpenTelemetry export must stand on their own.

Read `docs/PRODUCT_POSITIONING.md` and ADR 0006 before proposing scope that centers a vendor-specific workaround.

## Current implementation stage

Unless a task explicitly states otherwise, work only within **Stage 1 — Transparent Trace Proxy**:

- `GET /v1/models`;
- `POST /v1/chat/completions`;
- LM Studio upstream provider;
- non-streaming and SSE streaming preservation;
- request correlation;
- timing boundaries and exchange timeline;
- safe metadata capture;
- OpenTelemetry traces and metrics;
- SQLite local persistence;
- runtime health and diagnostics;
- minimal administrative APIs for traces/exchanges;
- contract, integration, architecture, and unit tests;
- optional minimal React dashboard only after the backend trace API is stable.

Stage 1 does **not** include:

- Laguna XML normalization;
- generic text-to-tool recovery;
- Anthropic protocol translation;
- prompt/schema compaction;
- cache-log parsing;
- replay execution;
- conformance orchestration beyond fixtures required to prove Stage 1 contracts;
- agentic coding evaluation;
- OpenCode or Cline plugins.

Interfaces may be prepared only where they simplify the current slice. Do not build speculative frameworks for later stages.

## Required reading order

1. `docs/PRODUCT_POSITIONING.md`
2. `docs/SPECIFICATION.md`
3. `docs/ARCHITECTURE.md`
4. `docs/ROADMAP.md`
5. `docs/API.md`
6. `docs/OBSERVABILITY.md`
7. `docs/CONFORMANCE.md`
8. `docs/REPLAY.md`
9. `docs/SECURITY.md`
10. `docs/TESTING.md`
11. relevant ADRs under `docs/adr/`

## Engineering constraints

- Target .NET 8 and C# 12.
- Enable nullable reference types.
- Use `System.Text.Json`; do not add Newtonsoft.Json without an accepted ADR.
- Use `TimeProvider` for time-dependent logic.
- Propagate `CancellationToken` through request and streaming paths.
- Use `HttpCompletionOption.ResponseHeadersRead` for upstream streaming.
- Do not buffer a complete streaming response in Stage 1.
- Do not write request or response bodies to default logs.
- Content persistence must be opt-in, sanitized, and independently retained.
- Use typed options with startup validation.
- Do not place domain or orchestration logic in endpoint lambdas/controllers.
- Keep provider-specific JSON isolated in provider adapters.
- Preserve unknown fields where safe and practical.
- Emit provenance for every measured, reported, inferred, or estimated metric.
- Avoid microservices. The initial product is a modular monolith.
- Do not add a broker, Kubernetes, or distributed worker system in Stage 1.
- Do not execute shell commands or model-generated tools in the gateway.

## Architecture boundaries

Expected initial projects:

```text
src/
  AgentSplice.Api/
  AgentSplice.Application/
  AgentSplice.Domain/
  AgentSplice.Infrastructure/
  AgentSplice.Protocols.OpenAI/
  AgentSplice.Providers.LmStudio/
  AgentSplice.Observability/

web/
  AgentSplice.Dashboard/              # Optional after trace API stability

tests/
  AgentSplice.UnitTests/
  AgentSplice.ContractTests/
  AgentSplice.IntegrationTests/
  AgentSplice.ArchitectureTests/
  AgentSplice.PerformanceTests/
```

Later modules may include:

```text
AgentSplice.Replay
AgentSplice.Conformance
AgentSplice.Evaluation
AgentSplice.Protocols.Anthropic
AgentSplice.Adapters.*
AgentSplice.Providers.*
```

Dependencies flow inward:

```text
Api -> Application
Infrastructure -> Application + Domain
Protocols/Providers/Observability -> Application contracts
Domain -> no infrastructure dependencies
```

The durable core must not reference Laguna, Qwen, OpenCode, LM Studio log formats, or any other vendor-specific type. Such behavior belongs in adapters.

## Stage 1 request-path rule

The Stage 1 request path is transparent by default:

1. validate the supported ingress envelope;
2. assign correlation identifiers;
3. resolve runtime and model alias;
4. record a safe structural summary;
5. forward without semantic rewriting;
6. incrementally parse and forward SSE;
7. record timing and protocol observations;
8. complete metadata persistence outside long-lived database transactions.

Any changed field must be required for routing, security, or protocol correctness and must be represented by a traceable event.

## Definition of done for every feature

A feature is complete only when it has:

- implementation;
- deterministic unit tests where applicable;
- contract or integration tests for public behavior;
- cancellation behavior;
- timeout behavior;
- structured logs without sensitive content;
- relevant metrics and provenance;
- configuration validation;
- documentation updates;
- security review against `docs/THREAT_MODEL.md`;
- no new compiler warnings;
- no disabled tests;
- no undocumented semantic transformation.

## API compatibility rules

- Return `x-agentsplice-request-id`.
- Preserve upstream status meaning when a stable client-facing equivalent exists.
- Translate transport failures into stable gateway error types.
- Keep raw credentials out of errors and traces.
- Preserve valid native tool calls as data; do not reinterpret ordinary content in Stage 1.
- Streaming output must remain valid SSE.
- `[DONE]` handling must be protocol-aware.
- Usage must retain provenance: client estimate, gateway estimate, upstream report, or runtime-log evidence.
- An HTTP 200 result must never be recorded as proof of full compatibility.

## Trace and observability rules

Every completion exchange must be able to represent:

- request accepted;
- validation complete;
- model/runtime resolved;
- upstream request opened;
- upstream headers received;
- first upstream byte;
- first semantic output event when observable;
- first client event flushed;
- upstream completion;
- client completion or cancellation;
- persistence completion/failure.

Do not infer prompt-processing completion from UI behavior alone. Do not label prompt tokens/s as generation tokens/s. Unknown values remain unknown.

## Adapter rule for later stages

A compatibility adapter requires:

- stable ID and version;
- exact activation conditions;
- test fixtures and evidence;
- transformation manifest;
- explicit failure policy;
- security analysis;
- upstream issue/PR reference when applicable;
- review date and retirement criteria.

Do not hard-code model-family behavior into generic request handling.

## Coding style

- Prefer explicit domain types over primitive parameter lists.
- Prefer small classes with one clear responsibility.
- Avoid service-locator patterns.
- Avoid static mutable state.
- Use async all the way through I/O paths.
- Use immutable records for captured observations when practical.
- Keep comments focused on rationale and invariants.
- Use stable event IDs and error codes.
- Avoid speculative abstractions that have only one hypothetical implementation.

## Test expectations

Stage 1 must include fixtures for:

- minimal model discovery;
- non-streaming chat;
- streaming chat;
- event boundaries split across reads;
- multiline SSE data;
- `[DONE]`;
- usage terminal chunks;
- malformed upstream JSON;
- premature EOF;
- client cancellation;
- upstream timeout phases;
- unknown-field preservation;
- metadata persistence failure;
- content-retention disabled by default;
- no prompt/response leakage in logs.

Use a deterministic fake upstream. Real LM Studio tests are optional local integration tests, never the only proof of behavior.

## Git and pull request behavior

- Keep commits and pull requests stage-scoped.
- Reference requirement IDs or ADRs.
- Include tests with behavior changes.
- Do not mix large formatting changes with functional work.
- State which measurements are from fake upstreams and which are hardware-dependent.
- Do not claim support without a conformance report.
- Prefer upstream contributions when investigation proves a defect belongs elsewhere.

## Expected commands after bootstrap

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

When the dashboard exists:

```powershell
pnpm install --frozen-lockfile
pnpm lint
pnpm test
pnpm build
```

## Scope control

Before coding, state:

1. current roadmap stage;
2. requirement IDs addressed;
3. files/modules expected to change;
4. tests to add;
5. explicit non-goals;
6. whether any semantic transformation is introduced.

Stop and ask for a scope decision when a task crosses a stage boundary or moves vendor-specific behavior into the durable core.
