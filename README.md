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

**Stage 0 — Repository foundation is complete.** The .NET 8 solution exists with enforced module
boundaries, the domain model for exchanges and evidence, validated configuration, a deterministic
fake upstream runtime, and CI on Windows and Ubuntu.

**Stage 1A — Transparent request path is complete.** `GET /v1/models` and non-streaming
`POST /v1/chat/completions` are served against an LM Studio runtime. Requests are forwarded without
semantic rewriting, request correlation is returned on every response, latency boundaries are
recorded where they were observed, and OpenTelemetry spans and metrics are emitted through
`System.Diagnostics`.

**Stage 1B — Streaming correctness and timeline is complete.** A `stream: true` request is relayed
byte for byte as it arrives, with SSE framing observed rather than rebuilt, the first upstream byte,
first decoded event, first semantic output event, and first client flush recorded as four separate
boundaries, and every way a stream can end classified rather than collapsed. Ten correctness defects
found in three review passes over 1A and 1B are corrected in
[ADR 0010](docs/adr/0010-correct-stream-boundary-and-termination-semantics.md),
[ADR 0011](docs/adr/0011-per-event-bounds-and-media-type-parsing.md), and
[ADR 0012](docs/adr/0012-classification-independent-of-relayability.md).

**Stage 1C — Metadata persistence and minimal dashboard is next.** There is no database and no
dashboard yet: evidence is built per request and handed to a sink that discards it, so nothing
survives the process. There are no administrative APIs for traces or exchanges, and no OpenTelemetry
SDK or exporter is referenced. Replay, conformance orchestration, evaluation, protocol translation,
and compatibility adapters all belong to later milestones.

No compatibility claim is made for any client, model, or runtime. An HTTP 200 is not evidence of
compatibility, and support claims require a dated conformance report (`docs/CONFORMANCE.md`).

See [Development guide](docs/DEVELOPMENT.md) to build and test.

## Stack

In use today: ASP.NET Core 8 and C# 12, System.Text.Json, Server-Sent Events, `System.Diagnostics`
spans and metrics, xUnit with architecture, contract, and integration suites. The rest is proposed
and arrives with the stage that needs it.

- ASP.NET Core 8 and C# 12
- System.Text.Json
- Server-Sent Events
- OpenTelemetry
- SQLite for local mode
- PostgreSQL for shared/self-hosted mode
- xUnit, architecture tests, integration tests, and Testcontainers
- Docker and Docker Compose
- React, TypeScript, and Vite for the optional local dashboard

## Protocol endpoints

Served today:

```http
GET  /v1/models
POST /v1/chat/completions      # non-streaming and SSE
```

Planned administrative APIs expose health, traces/exchanges, timeline metadata, and runtime diagnostics under `/api/v1`. They arrive with Stage 1C, alongside the persistence they read from. Content retention is disabled by default and nothing is retained at all today.

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
