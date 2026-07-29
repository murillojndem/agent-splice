# Benchmarking specification

## Purpose

The benchmark system determines whether a client/model/runtime/profile combination is correct and usable. It must not reduce evaluation to one throughput number.

## Scenario format

Each scenario includes:

- ID and semantic version;
- category;
- ingress protocol;
- request fixture;
- required model capabilities;
- profile under test;
- warm-up count;
- measured repetitions;
- timeout;
- correctness assertions;
- sensitive-data classification;
- expected tool names where applicable.

## Environment snapshot

Record:

- operating system;
- CPU;
- system memory;
- GPU and dedicated memory;
- runtime and backend;
- runtime version;
- driver/ROCm/CUDA version when observable;
- model repository and file name;
- model hash where practical;
- quantization;
- context length;
- GPU offload;
- KV cache configuration;
- Flash Attention;
- speculative/MTP settings;
- client and gateway versions.

## Core measurements

- success/failure;
- response protocol validity;
- tool-call validity;
- false-positive tool conversion;
- request bytes;
- estimated prompt tokens;
- upstream-reported prompt/completion tokens;
- TTFT;
- prompt processing time;
- generation time;
- total time;
- prompt tokens/s;
- generation tokens/s;
- peak dedicated/shared GPU memory when available;
- CPU and GPU utilization samples when available.

## Statistical treatment

- publish each iteration;
- report median and percentiles;
- distinguish cold and warm;
- do not discard failed runs silently;
- identify outlier rule;
- use identical fixtures for comparisons;
- disclose configuration differences.

## Agentic coding suite — later stage

Coding scenarios should use synthetic or open-source repositories in disposable containers. Each task defines an initial commit, instruction, test command, allowed tools, time limit, and expected outcome. Never use confidential employer code or incidents.
