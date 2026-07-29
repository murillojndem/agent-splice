# Roadmap

The roadmap is outcome-based. Dates are intentionally omitted until the maintainer estimates the first implementation slices.

## Stage 0 — Repository foundation

- Create .NET solution and projects.
- Configure analyzers, nullable, warnings-as-errors in CI, formatting, and architecture tests.
- Add GitHub Actions for Windows and Ubuntu.
- Add fake upstream server test fixture.
- Validate configuration objects at startup.
- Publish initial OpenAPI description.
- Add Dockerfile and Compose development stack.

## Stage 1A — OpenAI-compatible LM Studio proxy

- `GET /v1/models`.
- `POST /v1/chat/completions`, non-streaming.
- Runtime endpoint configuration.
- Model aliases.
- Stable error translation.
- Correlation IDs and traces.
- Unit and contract tests.

## Stage 1B — Streaming correctness

- SSE parser.
- SSE writer.
- `ResponseHeadersRead` upstream client.
- cancellation and disconnect propagation;
- separate timeout phases;
- TTFT and stream metrics;
- malformed/truncated stream fixtures.

## Stage 1C — Tool-call normalization

- Native structured passthrough.
- Content-JSON detector and parser.
- Laguna XML normalizer.
- Profile-driven enablement.
- Tool-name and JSON Schema validation.
- Transformation timeline.
- False-positive and adversarial fixtures.

## Stage 1D — Persistence and first release

- SQLite metadata store.
- PostgreSQL provider.
- retention policies;
- Docker image;
- release workflow;
- sample OpenCode and Cline configurations;
- first compatibility report.

## Stage 2A — Anthropic compatibility

- Messages request/response models.
- streaming content-block state machine.
- tool-use and tool-result mapping.
- translation report.

## Stage 2B — Prompt and tool-schema compaction

- token estimation adapters;
- deterministic description trimming;
- duplicate schema elimination;
- optional example removal;
- before/after validation corpus;
- per-profile policy.

## Stage 2C — Cache diagnostics

- cache evidence model;
- runtime-log import adapter;
- repeated-prefix benchmark;
- cold/warm visualization;
- probable hit/miss classification.

## Stage 2D — Replay and dashboard

- sanitization pipeline;
- immutable replay artifacts;
- exact and adapted replay;
- React dashboard;
- exchange timeline;
- transformation details.

## Stage 2E — Automated benchmark system

- scenario schema;
- execution runner;
- result persistence;
- environment fingerprints;
- comparison report;
- CI-safe protocol benchmark suite.

## Stage 3A — OpenCode integration

- provider setup helper or plugin;
- compact tools/profile negotiation;
- title-generation routing options;
- gateway diagnostics surfaced in client;
- upstream PRs where changes belong in OpenCode.

## Stage 3B — Cline integration

- local provider guide;
- compact prompt compatibility tests;
- diagnostics display exploration;
- upstream PRs where appropriate.

## Stage 3C — Model support packs

- Laguna XS support pack.
- Qwen 3.6 dense/MTP/MoE support packs.
- fixture provenance.
- profile versioning.
- community contribution validation.

## Stage 3D — Backend comparison laboratory

- AMD ROCm environment capture.
- Vulkan environment capture where loadable.
- community CUDA result import.
- fixed model/quantization scenarios.
- memory and throughput reports.

## Stage 3E — Upstream program

- issue bundle generator;
- minimal reproduction repositories;
- llama.cpp tests and patches;
- OpenCode/Cline compatibility patches;
- LM Studio documentation or bug reports;
- accepted contribution index.

## Deferred possibilities

- OpenAI Responses API.
- MCP gateway or policy plane.
- multi-tenant hosted mode.
- distributed benchmark workers.
- Kubernetes deployment.
- semantic tool selection.
- encrypted content vault.
- dynamic plugin marketplace.
