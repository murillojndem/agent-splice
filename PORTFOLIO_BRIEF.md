# Portfolio brief

## One-sentence description

AgentSplice is an open-source .NET gateway that makes local AI coding agents reliable across inconsistent runtime APIs, tool-call formats, streaming implementations, cache behavior, and GPU backends.

## Technical proof points to target

- OpenAI-compatible streaming proxy implemented in ASP.NET Core 8.
- Incremental SSE parsing without full response buffering.
- Profile-driven conversion of text-encoded tool calls into validated structured calls.
- OpenTelemetry traces separating gateway overhead, TTFT, prompt processing, and generation.
- SQLite/PostgreSQL metadata persistence with content retention disabled by default.
- Automated compatibility and performance benchmarks.
- Reproducible AMD/ROCm issue reports and upstream pull requests.

## Evidence expected before claiming completion

- public repository;
- architecture and threat model;
- CI status;
- tagged releases;
- benchmark reports;
- demo video or screenshots;
- accepted upstream issues/PRs;
- real integration with at least one agent client and one local runtime.
