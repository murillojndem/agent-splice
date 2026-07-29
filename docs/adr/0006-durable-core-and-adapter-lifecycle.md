# ADR 0006: Durable core and adapter lifecycle

- Status: Accepted
- Date: 2026-07-29

## Context

The original AgentSplice concept emphasized tool-call normalization and workarounds for current incompatibilities between agent clients, model runtimes, and model templates. Those defects may be fixed upstream. A product whose primary value is one workaround can become unnecessary after normal product iteration by OpenCode, LM Studio, llama.cpp, or model authors.

## Decision

AgentSplice's durable core is:

1. transparent trace capture;
2. timeline reconstruction and observability;
3. sanitized replay and differential comparison;
4. protocol and behavioral conformance;
5. agent-task evaluation and regression testing;
6. portable evidence and OpenTelemetry export.

Protocol translation, prompt compaction, text-to-tool recovery, model profiles, runtime-log parsers, and vendor-specific workarounds are optional adapters around this core.

Every compatibility adapter must declare:

- stable ID and version;
- activation constraints;
- supported client/runtime/model versions;
- evidence and fixtures;
- transformation behavior;
- failure policy;
- security implications;
- owner or maintainer;
- upstream issue or PR when applicable;
- review and retirement criteria.

## Consequences

- Stage 1 prioritizes transparent trace proxying and observability rather than text-to-tool normalization.
- Replay and conformance arrive before broad normalization support.
- The first public release does not require a vendor-specific workaround.
- Adapters may be retired after an upstream fix without changing the core product.
- Product claims emphasize evidence, evaluation, and interoperability rather than permanent correction of third-party defects.
- The architecture must retain raw-versus-forwarded structural metadata where privacy policy permits so transformations are auditable.

## Rejected alternatives

### Make normalization the permanent product center

Rejected because upstream fixes can remove the need and because aggressive normalization introduces correctness and security risk.

### Build a general LLM provider router

Rejected because established projects already cover broad routing, billing, and commercial-provider concerns. AgentSplice differentiates through agent-focused trace, conformance, replay, and evaluation.

### Remove all normalization features

Rejected because deterministic, profile-gated adapters remain useful for diagnosis, temporary interoperability, and producing upstream-quality reproductions.
