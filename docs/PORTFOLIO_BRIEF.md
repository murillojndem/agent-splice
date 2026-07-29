# Portfolio brief

## One-sentence description

AgentSplice is an open-source .NET platform that traces, replays, validates, and evaluates interactions between AI agents and local or self-hosted model runtimes.

## Positioning for employment

The project is intended to demonstrate applied-AI systems engineering rather than foundation-model research. It connects existing backend experience to roles such as:

- Applied AI Engineer;
- AI Backend Engineer;
- Agent Infrastructure Engineer;
- LLM Platform Engineer;
- AI Developer Tools Engineer;
- Model Evaluation Engineer;
- Inference/Observability Engineer.

## Durable technical proof points

- OpenAI-compatible streaming proxy implemented in ASP.NET Core 8.
- Incremental SSE parsing without full-response buffering.
- Trace timeline separating gateway overhead, upstream headers, first byte, first semantic token, stream delivery, and completion.
- OpenTelemetry spans and metrics with explicit provenance.
- Sanitized replay with exact versus adapted manifests.
- Differential comparison across models, runtimes, profiles, and versions.
- Protocol, streaming, cancellation, usage, and tool-call conformance suites.
- SQLite/PostgreSQL persistence with raw content disabled by default.
- Agentic coding evaluations executed in disposable environments.
- Regression reports suitable for CI and upstream issue evidence.
- Optional, profile-driven compatibility adapters with retirement criteria.
- Reproducible AMD/ROCm measurements and upstream contributions.

## Strong public demonstration

A compelling demo should show one captured OpenCode or Cline interaction, then:

1. reconstruct its timeline;
2. distinguish prefill from generation and gateway overhead;
3. display a malformed or unsupported tool-call condition without silently rewriting it;
4. create a sanitized replay artifact;
5. replay the same request against two runtime/model configurations;
6. run a conformance suite;
7. export an issue bundle;
8. show a regression comparison between two versions.

## Claims to avoid until proven

Do not claim:

- universal OpenAI or Anthropic compatibility;
- production readiness before threat-model and load evidence;
- lossless protocol translation;
- cache hits without observable evidence;
- tool-call correctness based only on model text;
- AMD kernel expertise without accepted low-level contributions;
- benchmark superiority from one machine or one run.

## Evidence expected before claiming completion

- public repository and license;
- architecture, threat model, and ADRs;
- Windows and Linux CI;
- tagged releases;
- recorded conformance reports;
- replay demonstration;
- benchmark environment snapshots;
- screenshots or short technical demo;
- integration with at least one real agent client and one local runtime;
- at least one high-quality upstream issue or accepted PR;
- written analysis of one regression or interoperability defect.
