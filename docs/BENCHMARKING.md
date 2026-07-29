# Benchmarking and evaluation specification

## Purpose

The benchmark and evaluation subsystem determines whether a client/model/runtime/profile/backend combination is correct, usable, and stable. It must not reduce evaluation to one throughput number.

The system has three related but distinct layers:

1. **Conformance** — does the implementation obey a protocol or behavioral contract?
2. **Performance benchmark** — how much time and resource does a controlled interaction consume?
3. **Agent-task evaluation** — does the complete agent workflow accomplish a verifiable task safely?

Results from one layer must not be presented as results from another.

## Scenario format

Each immutable scenario version includes:

- scenario ID and semantic version;
- category and evaluation layer;
- ingress protocol;
- request or repository fixture;
- required capabilities;
- runtime/model/profile selectors;
- adapter policy;
- warm-up count;
- measured repetitions;
- timeout and cancellation policy;
- allowed tools;
- network policy;
- correctness assertions;
- sensitive-data classification;
- expected tool names or file effects where applicable;
- result-retention policy.

## Environment snapshot

Record:

- operating system and build;
- CPU;
- system memory;
- GPU and dedicated memory;
- runtime and backend;
- runtime version/commit;
- driver, ROCm, CUDA, or Vulkan version when observable;
- model repository and filename;
- model hash where practical;
- quantization;
- context length;
- GPU offload;
- KV cache configuration;
- Flash Attention;
- speculative/MTP settings;
- concurrency/slot count;
- client and AgentSplice versions;
- profile/adapter versions;
- relevant environment variables with secrets removed;
- cold/warm runtime state.

## Core protocol/performance measurements

- pass, fail, skipped, unsupported, or inconclusive;
- response protocol validity;
- SSE validity;
- tool-call validity;
- false-positive adapter rate;
- request/response bytes;
- estimated and reported token counts with provenance;
- time to response headers;
- time to first upstream byte;
- time to first semantic event;
- time to first client event;
- prompt-processing time where observable;
- generation time where observable;
- total wall-clock time;
- prompt tokens/s;
- generation tokens/s;
- gateway-only overhead;
- adapter overhead;
- peak dedicated/shared GPU memory when available;
- CPU/GPU utilization samples when available;
- error/failure layer.

## Agent-task measurements

- task completed;
- build succeeded;
- tests passed/failed;
- hidden assertions passed/failed;
- number of model turns;
- number of tool calls;
- valid versus invalid tool calls;
- retries and recoveries;
- files changed;
- prohibited files changed;
- commands executed;
- unauthorized network attempts;
- wall-clock time;
- token use and provenance;
- runtime failures;
- human intervention required;
- final repository diff hash.

## Initial protocol scenarios

- `simple_text_001`: short prompt, no tools;
- `long_prefill_001`: fixed long prompt and short answer;
- `long_generation_001`: short prompt and bounded long answer;
- `stream_split_bytes_001`: SSE frames split across reads;
- `stream_multiline_001`: multiline SSE data;
- `stream_cancel_001`: client cancellation;
- `tool_native_single_001`: native structured call;
- `tool_native_multiple_001`: ordered multiple calls;
- `tool_stream_fragments_001`: arguments split across events;
- `tool_false_positive_001`: prose discussing tool syntax;
- `cache_second_turn_001`: repeated stable prefix with short suffix;
- `runtime_malformed_stream_001`: invalid upstream event.

## Initial coding-task suite

Coding scenarios use synthetic or open-source repositories in disposable containers. Never use confidential employer code, incidents, credentials, customer data, or proprietary specifications.

Suggested first tasks:

1. fix a failing ASP.NET Core endpoint test;
2. add an EF Core migration from a written requirement;
3. repair a PostgreSQL query with a deterministic fixture;
4. add xUnit coverage for an existing bug;
5. update a Docker Compose service while preserving health checks;
6. perform a constrained refactor with behavior-preserving tests;
7. diagnose a structured log and apply a minimal code fix;
8. reject a request that requires changing files outside the allowed scope.

Each task defines:

- initial repository commit or archive hash;
- instruction text;
- allowed tools;
- forbidden paths;
- build/test command;
- hidden assertions;
- time and turn limits;
- expected outcome;
- cleanup behavior.

## Statistical treatment

- publish each measured iteration;
- report median and relevant percentiles;
- distinguish cold and warm runs;
- do not discard failed runs silently;
- declare outlier rules before analysis;
- use identical fixtures for comparisons;
- disclose every configuration difference;
- separate correctness ranking from performance ranking;
- avoid universal claims from one machine;
- record unsupported configurations rather than omitting them.

## Regression comparison

A regression report compares a candidate against an approved baseline and may include:

- new conformance failures;
- task-success delta;
- tool-validity delta;
- latency-phase deltas;
- throughput deltas;
- failure-rate delta;
- memory delta;
- changed adapter behavior;
- changed environment dimensions.

Relative thresholds are preferable to brittle absolute hardware values. Correctness regressions may block promotion even when performance improves.

## Benchmark integrity

- fixtures are versioned and immutable after publication;
- scenario changes require a new version;
- raw result artifacts retain hashes;
- manual exclusions are recorded;
- content sanitization occurs before export;
- no score may hide unsupported or failed runs;
- estimates must not be mixed with measured values without labeling;
- deterministic fake-runtime benchmarks must be distinguished from real inference.
