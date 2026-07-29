# Changelog

All notable changes will be documented here.

## Unreleased

### Stage 0 — Repository foundation

- Bootstrapped the .NET 8 solution with the seven Stage 1 production projects and enforced the
  dependency rules from `docs/ARCHITECTURE.md` with architecture tests.
- Pinned the SDK feature band, centralised package versions, promoted warnings to errors, and moved
  formatting enforcement to a separate CI step (ADR 0007).
- Added the Stage 0 domain model: exchanges, append-only timelines, observations with bounded
  sanitised detail, measurements with mandatory provenance, and throughput derivation that returns
  unknown rather than zero when the evidence is insufficient.
- Added typed configuration with startup validation, including duplicate and cyclic alias detection
  (FR-MOD-006), timeout-phase coherence, credential-free runtime URLs, and a guard that refuses to
  start when the Stage 4 adapter flag is enabled.
- Added stable Stage 1 error codes and the normative OpenTelemetry names, both verified against the
  documents that declare them by contract tests.
- Added the deterministic fake upstream runtime: a real loopback Kestrel listener with byte-exact SSE
  scripting, arbitrary chunk boundaries, delayed headers, trailing stalls, premature close, request
  recording, and upstream-side cancellation observation.
- Added Windows and Ubuntu CI, a real multi-stage Dockerfile running as a non-root user, a
  `.dockerignore` that keeps host build output out of the image context, and loopback-bound Compose
  defaults.
- Made the loopback binding default a fallback rather than an `appsettings.json` value. Declared in the
  settings file it layered over `ASPNETCORE_URLS`, so the container bound loopback inside itself and
  the published port mapping was unreachable.
- Corrected the deployment environment variables to the double-underscore form that actually binds to
  the `agentsplice` configuration section, and added a contract test that fails when a variable binds
  to nothing.
- Declared the three Stage 1 administrative endpoints that `docs/API.md` documented but the OpenAPI
  draft omitted.
- Verified the Stage 0 exit criterion: a streamed fake-upstream exchange is fully representable by the
  domain model, with unobserved boundaries staying absent rather than becoming zero.

### Revised product thesis

- Repositioned AgentSplice from a protocol-normalizing gateway to a local-first interoperability, observability, replay, conformance, and evaluation platform.
- Defined trace, replay, conformance, evaluation, regression, and OpenTelemetry export as the durable core.
- Moved tool-call recovery, prompt/schema compaction, protocol translation, model-specific rules, and runtime workarounds to optional later-stage adapters.
- Reordered the roadmap so transparent trace proxying comes first, followed by replay/conformance, agent evaluation, adapters, and ecosystem/backend work.
- Added product-positioning, conformance, replay, and adapter-lifecycle documents.
- Updated architecture, API, security, testing, observability, benchmarking, portfolio, and implementation-agent instructions.
- Expanded the OpenAPI draft with exchange and administrative trace endpoints.

### Original specification pack

- Created specification-first repository pack.
- Defined AgentSplice name, initial architecture, roadmap, API draft, security model, and implementation instructions.
