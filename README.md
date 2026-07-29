# AgentSplice

**Local agent interoperability, observability, and evaluation.**

AgentSplice is a local-first platform for tracing, replaying, comparing, and evaluating interactions between AI-agent clients and model runtimes.

It sits between clients such as OpenCode, Cline, Aider, IDE extensions, test harnesses, and automation scripts, and runtimes such as LM Studio, llama.cpp, Ollama, vLLM, SGLang, or other compatible services.

```text
Agent client
  OpenCode / Cline / Aider / custom harness
          |
          | OpenAI or Anthropic-compatible request
          v
+------------------------------------------------+
|                  AgentSplice                   |
| trace capture and timeline reconstruction      |
| protocol and tool-call conformance             |
| sanitized replay and differential comparison  |
| agent evaluation and regression testing        |
| OpenTelemetry export and local dashboard       |
| optional compatibility adapters                |
+------------------------------------------------+
          |
          | transparent or explicitly adapted request
          v
Model runtime
  LM Studio / llama.cpp / Ollama / vLLM / SGLang
```

## Why this project exists

Agent workflows fail across several independent boundaries: client schemas, provider adapters, prompt templates, streaming parsers, model behavior, tool-call representations, cache mechanisms, runtime versions, and hardware backends. The user interface often collapses all of those failures into “the model is slow” or “tools do not work.”

AgentSplice provides evidence instead of guesswork. It is intended to answer:

- What did the client actually send?
- What did the runtime actually receive?
- Which fields or events changed?
- Where was wall-clock time spent?
- Was a tool call structurally valid or merely printed as text?
- Was prefix reuse probable, absent, or unknown?
- Does the same request behave differently across models, runtimes, backends, or versions?
- Did an update introduce a correctness or performance regression?
- Can the behavior be replayed and attached to an upstream issue?

## Durable core versus optional adapters

The durable product core is:

1. transparent trace capture;
2. observability and timeline reconstruction;
3. sanitized replay;
4. protocol and behavioral conformance;
5. agent-task evaluation and regression testing;
6. portable evidence and OpenTelemetry export.

Tool-call recovery, Qwen/Laguna-specific rules, OpenAI/Anthropic translation, prompt/schema compaction, cache-log parsers, and runtime workarounds are optional, versioned adapters. The project must remain useful when those defects are fixed upstream.

See [Product positioning](docs/PRODUCT_POSITIONING.md) and [ADR 0006](docs/adr/0006-durable-core-and-adapter-lifecycle.md).

## Status

This repository is specification-first. Implementation is intentionally staged.

The first milestone is a transparent OpenAI-compatible trace proxy for LM Studio with correct non-streaming and SSE behavior, request correlation, metadata capture, latency boundaries, OpenTelemetry, SQLite, and a minimal local dashboard. It does **not** require vendor-specific tool-call normalization.

Later milestones add replay, conformance suites, differential comparison, agentic evaluations, protocol adapters, compatibility adapters, model support packs, client integrations, backend comparisons, and upstream contributions.

## Proposed stack

- ASP.NET Core 8 and C# 12
- System.Text.Json
- Server-Sent Events
- OpenTelemetry
- SQLite for local mode
- PostgreSQL for shared/self-hosted mode
- xUnit, architecture tests, integration tests, and Testcontainers
- Docker and Docker Compose
- React, TypeScript, and Vite for the optional local dashboard

## Initial protocol endpoints

```http
GET  /v1/models
POST /v1/chat/completions
```

Initial administrative APIs expose health, traces/exchanges, timeline metadata, and runtime diagnostics under `/api/v1`. Content retention remains disabled by default.

## Documentation

- [Complete product and engineering specification](docs/SPECIFICATION.md)
- [Product positioning](docs/PRODUCT_POSITIONING.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Roadmap](docs/ROADMAP.md)
- [Conformance specification](docs/CONFORMANCE.md)
- [Replay specification](docs/REPLAY.md)
- [Benchmarking and evaluation](docs/BENCHMARKING.md)
- [Observability](docs/OBSERVABILITY.md)
- [API contract notes](docs/API.md)
- [Claude implementation contract](CLAUDE.md)
- [Threat model](docs/THREAT_MODEL.md)
- [Portfolio brief](docs/PORTFOLIO_BRIEF.md)

## Design principles

1. Evidence before workaround.
2. Transparent capture before transformation.
3. Replay and conformance before broad compatibility rewriting.
4. Local-first privacy and loopback-only defaults.
5. Never execute model-requested tools inside the core gateway.
6. Distinguish measured, reported, inferred, and estimated values.
7. Preserve streaming and cancellation semantics.
8. Treat transformations as explicit, versioned, auditable policies.
9. Treat AMD/ROCm and other non-CUDA environments as first-class evidence sources.
10. Upstream fixes when a defect belongs in another project.

## Product boundary

AgentSplice is not another chat, coding agent, model runtime, model downloader, general cloud billing router, or permanent workaround collection. It is an independent diagnostic and evaluation plane for agent/runtime interactions.

## License

Apache License 2.0 is proposed. Review [LICENSE-CHOICE.md](LICENSE-CHOICE.md) before publication.
