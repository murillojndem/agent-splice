# Revision notes — durable product repositioning

Date: 2026-07-29

## Decision incorporated

The specification no longer treats tool-call normalization or an OpenCode/LM Studio workaround as AgentSplice's primary value.

The durable core is now:

1. transparent trace capture;
2. observability and measurement provenance;
3. sanitized replay and differential comparison;
4. protocol and behavioral conformance;
5. agent-task evaluation and regression testing;
6. portable reports and OpenTelemetry export.

Protocol translation, prompt/schema compaction, model-specific recovery, runtime-log parsing, and vendor/version workarounds are optional later-stage adapters with lifecycle and retirement rules.

## New files

- `docs/PRODUCT_POSITIONING.md`
- `docs/CONFORMANCE.md`
- `docs/REPLAY.md`
- `docs/adr/0006-durable-core-and-adapter-lifecycle.md`
- `REVISION_NOTES.md`

## Substantially revised files

- `README.md`
- `CLAUDE.md`
- `AGENTS.md`
- `docs/SPECIFICATION.md`
- `docs/ROADMAP.md`
- `docs/ARCHITECTURE.md`
- `docs/API.md`
- `docs/OBSERVABILITY.md`
- `docs/BENCHMARKING.md`
- `docs/TESTING.md`
- `docs/SECURITY.md`
- `docs/THREAT_MODEL.md`
- `docs/COMPATIBILITY_MATRIX.md`
- `docs/CONTRIBUTING_PLAN.md`
- `docs/PORTFOLIO_BRIEF.md`
- `docs/GLOSSARY.md`
- `openapi/agentsplice-openapi.yaml`
- `config/profiles/*.yaml`
- `CHANGELOG.md`
- `src/README.md`
- `tests/README.md`

## Roadmap change

Old order:

1. proxy;
2. streaming;
3. tool-call normalization;
4. persistence;
5. replay/dashboard/benchmarks later.

New order:

1. transparent trace proxy;
2. streaming timeline, persistence, and minimal dashboard;
3. replay and conformance;
4. agent evaluation and regression;
5. optional interoperability adapters;
6. integrations, backend laboratory, and upstream contribution program.

## Implementation instruction

Claude must implement only the active roadmap stage. Stage 1 explicitly excludes text-to-tool recovery, Anthropic translation, prompt compaction, cache-log parsing, replay execution, and agentic evaluation.
