# Architecture

## Architectural style

AgentSplice starts as a modular monolith. The request path is latency-sensitive and benefits from in-process module calls, while the codebase still needs explicit boundaries for protocols, providers, normalization, persistence, and observability.

## Solution structure

```text
AgentSplice.sln
src/
  AgentSplice.Api
  AgentSplice.Application
  AgentSplice.Domain
  AgentSplice.Infrastructure
  AgentSplice.Protocols.OpenAI
  AgentSplice.Protocols.Anthropic        # Stage 2
  AgentSplice.Providers.LmStudio
  AgentSplice.Providers.LlamaCpp         # Later
  AgentSplice.Normalization
  AgentSplice.Observability
tests/
  AgentSplice.UnitTests
  AgentSplice.ContractTests
  AgentSplice.IntegrationTests
  AgentSplice.ArchitectureTests
  AgentSplice.PerformanceTests
```

## Dependency rules

- Domain references no project.
- Application references Domain.
- Protocol modules reference Application contracts and their own DTOs.
- Provider modules implement Application ports.
- Infrastructure implements persistence and hosting-adjacent ports.
- API composes modules and does not contain transformation logic.
- Normalization contains deterministic strategies and no HTTP concerns.

## Primary request flow

1. HTTP request enters an ingress protocol adapter.
2. Request limits and authentication run.
3. Protocol DTO is validated.
4. The application resolves model alias, runtime, and profile.
5. Request transformations execute and emit events.
6. Provider adapter creates the upstream request.
7. Upstream response is decoded.
8. Response normalization executes.
9. Client protocol events are written.
10. Metrics and optional metadata persistence complete.

## Streaming architecture

Use `HttpCompletionOption.ResponseHeadersRead`. Parse SSE incrementally from the upstream stream. The output writer should flush complete semantic events, not arbitrary bytes. Normalizers that require partial buffering operate through a bounded per-request state machine.

Suggested abstractions:

```csharp
public interface ICompletionGateway
{
    Task<CompletionResult> CompleteAsync(...);
    IAsyncEnumerable<CompletionStreamEvent> StreamAsync(...);
}

public interface IModelRuntimeProvider
{
    Task<IReadOnlyList<DiscoveredModel>> ListModelsAsync(...);
    Task<ProviderCompletion> CompleteAsync(...);
    IAsyncEnumerable<ProviderStreamEvent> StreamAsync(...);
}

public interface IProtocolRequestMapper<TRequest>
{
    CanonicalCompletionRequest Map(TRequest request);
}

public interface IProtocolResponseMapper<TResponse>
{
    TResponse Map(CanonicalCompletionResponse response);
}
```

A canonical model should include only concepts that AgentSplice can define consistently. Unknown provider fields may be retained in an extension bag, but the canonical model must not pretend all protocols are losslessly interchangeable.

## Canonical completion model

Recommended concepts:

- model selector;
- ordered messages/content blocks;
- tools;
- tool choice;
- generation options;
- streaming preference;
- metadata/extensions;
- response content blocks;
- structured tool calls;
- finish reason;
- usage with provenance.

## Provider isolation

LM Studio-specific behavior includes model discovery shape, optional headers, timeout behavior, and runtime metadata. It must not leak into endpoint controllers or domain entities.

## Persistence

Use EF Core only in infrastructure. Keep completion streaming independent of database transaction duration. Persist summary metadata after or alongside the stream through a bounded channel. Define behavior for persistence failure explicitly.

## Background processing

Stage 1 background services:

- model discovery refresh;
- retention cleanup;
- optional metadata persistence queue;
- health refresh.

Replay and benchmark workers arrive later.

## Extensibility

Extension points:

- providers;
- ingress/egress protocols;
- model profile loaders;
- request transforms;
- response normalizers;
- token estimators;
- hardware telemetry collectors;
- replay sanitizers;
- benchmark validators.

Avoid generic plugin loading in Stage 1. Use compile-time registration first. Dynamic community plugins increase security and versioning complexity and require a later ADR.
