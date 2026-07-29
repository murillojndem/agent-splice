# Product positioning

## Durable product thesis

AgentSplice is a **local-first interoperability, observability, replay, conformance, and evaluation platform for AI agents**.

It is not defined by one defect in OpenCode, LM Studio, llama.cpp, Qwen, Laguna, ROCm, or any other current dependency. Those defects are useful initial test cases, but they are not the product boundary.

The durable product value is the ability to answer, with reproducible evidence:

- what an agent client actually sent;
- what a runtime actually received;
- which protocol fields or stream events changed in transit;
- where latency was spent;
- whether a tool call was structurally valid;
- whether a repeated request reused a stable prefix;
- whether the same interaction behaves differently across models, runtimes, backends, or versions;
- whether an update introduced a correctness or performance regression;
- whether an observed failure belongs to the client, gateway, model, runtime, template, parser, or hardware backend.

## Primary analogy

AgentSplice should become a combination of:

- **Wireshark for agent/runtime interactions** — captures and explains the exchange;
- **Postman for agent protocols** — replays and compares requests;
- **a conformance laboratory** — verifies OpenAI-, Anthropic-, SSE-, and tool-call behavior;
- **an evaluation runner** — measures whether complete agent tasks succeed;
- **an interoperability layer** — optionally adapts incompatible representations when an explicit, tested rule exists.

No analogy is exact. The project remains an application-layer system specialized for LLM agents and inference runtimes.

## Core capabilities that remain useful after upstream fixes

### Trace capture and timeline reconstruction

Capture request metadata, upstream metadata, stream events, timing boundaries, usage provenance, tool-call lifecycle, cancellation, errors, and optional sanitized payloads. Produce a timeline that separates client behavior, gateway overhead, runtime prefill, generation, and downstream delivery.

### Replay and differential comparison

Replay a sanitized interaction against the same or a different model, runtime, profile, backend, or version. Distinguish exact replay from adapted replay and disclose every adaptation.

### Protocol and behavioral conformance

Run deterministic suites covering request schemas, SSE framing, tool calls, streaming argument assembly, finish reasons, cancellation, usage reporting, unknown fields, and malformed upstream behavior.

### Agent evaluation and regression testing

Execute complete tasks in isolated repositories or controlled fixtures. Measure task success, tests passed, tool validity, unauthorized changes, latency, iterations, and resource use.

### Neutral observability

Export OpenTelemetry traces and metrics without requiring a specific agent client or runtime vendor. Preserve the distinction between measured, upstream-reported, inferred, and estimated values.

## Replaceable compatibility modules

The following are useful but intentionally non-core:

- Laguna XML tool-call recovery;
- Qwen-specific tool templates;
- OpenCode prompt/schema compaction;
- LM Studio-specific cache-log parsing;
- OpenAI-to-Anthropic translation;
- runtime-version workarounds;
- backend-specific diagnostics.

These capabilities must be implemented as versioned adapters or policies with evidence, tests, activation constraints, and retirement criteria. When an upstream fix makes one adapter unnecessary, AgentSplice should disable, simplify, or remove it without changing the product thesis.

## Competitive boundary

AgentSplice must not become a general cloud-provider router or billing gateway. Products such as generic LLM proxies already cover provider selection, retries, budgets, and API-key management. AgentSplice may interoperate with them, but its primary differentiation is **agent interaction forensics, conformance, replay, and evaluation**, particularly for local and self-hosted inference.

## Target users

- developers running local or self-hosted coding agents;
- maintainers diagnosing client/runtime/model compatibility defects;
- applied AI teams evaluating agent stacks before adoption;
- inference and performance engineers comparing backends;
- open-source contributors preparing reproducible issues and pull requests;
- teams requiring local retention and inspectable evidence.

## Product statements

### One-line description

AgentSplice is a local-first platform for tracing, replaying, comparing, and evaluating interactions between AI agents and model runtimes.

### Extended description

AgentSplice captures agent-to-runtime traffic, reconstructs streaming timelines, validates protocol and tool-call behavior, replays interactions across models and runtimes, and runs reproducible conformance and agent-task evaluations. Optional adapters can normalize known incompatibilities, but the product remains useful when those incompatibilities are fixed upstream.

### Tagline

**Local agent interoperability, observability, and evaluation.**

## What AgentSplice is not

- not another chat interface;
- not an autonomous coding agent;
- not a model runtime;
- not a model downloader;
- not an MCP tool executor in its core request path;
- not merely a proxy for LM Studio;
- not merely a tool-call parser;
- not a permanent collection of vendor-specific workarounds;
- not a claim that every LLM protocol can be translated losslessly.

## Strategic success condition

AgentSplice remains valuable when OpenCode and LM Studio work perfectly together. In that future, it should still provide independent traces, replay, differential testing, conformance reports, benchmark history, CI regression gates, and portable evidence across competing clients and runtimes.
