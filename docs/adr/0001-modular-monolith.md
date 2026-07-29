# ADR 0001: Begin as a modular monolith

Status: Accepted

## Context

The gateway has latency-sensitive streaming behavior but several clear domains. Early microservices would add deployment, tracing, failure, and versioning cost before independent scaling requirements exist.

## Decision

Use a modular monolith with separate .NET projects and enforced dependency boundaries.

## Consequences

- Lower operational complexity.
- In-process streaming pipeline.
- Modules can later be extracted if benchmarks and deployment needs justify it.
