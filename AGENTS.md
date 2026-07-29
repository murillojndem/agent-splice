# AGENTS.md

Instructions for coding agents working in this repository.

## Repository purpose

Build AgentSplice as a local-first platform for tracing, replaying, validating, comparing, and evaluating interactions between AI-agent clients and model runtimes.

The durable core is observability, replay, conformance, and evaluation. Compatibility normalization is optional adapter behavior, not the product center.

## Required priorities

1. Correct transparent proxy behavior.
2. Accurate timeline and provenance data.
3. Valid SSE streaming, cancellation, and timeout semantics.
4. Privacy-preserving metadata capture.
5. Reproducible replay and conformance fixtures.
6. Small, reviewable delivery increments.
7. Optional transformations that are explicit, versioned, and reversible where possible.

## Never do automatically

- Execute model-generated tools.
- Persist raw prompts, responses, source code, or tool arguments without explicit opt-in.
- Treat HTTP 200 as proof of compatibility.
- Label estimated values as measured values.
- Silently discard unknown protocol fields.
- Silently change model selection, context length, tool schemas, or generation parameters.
- Add cloud dependencies to the core request path.
- implement a vendor-specific workaround in the core domain;
- merge multiple roadmap stages into one task;
- optimize before a baseline trace and test exist.

## Adapter rule

A client-, model-, runtime-, protocol-, or backend-specific behavior belongs in a versioned adapter or policy with fixtures, evidence, activation constraints, failure behavior, and retirement criteria.

## Repository map

Read, in order:

1. `docs/PRODUCT_POSITIONING.md`
2. `docs/SPECIFICATION.md`
3. `docs/ARCHITECTURE.md`
4. `docs/ROADMAP.md`
5. `docs/CONFORMANCE.md`
6. `docs/REPLAY.md`
7. `CLAUDE.md`
