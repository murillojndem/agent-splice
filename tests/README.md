# Test projects

Test projects follow `docs/TESTING.md`.

## Present

| Project | Covers |
| --- | --- |
| `AgentSplice.TestSupport` | Deterministic fake upstream runtime and repository path helpers. Not a test project itself. |
| `AgentSplice.UnitTests` | Domain rules, timeline absence rules, measurement provenance, configuration validation, privacy defaults. |
| `AgentSplice.ContractTests` | Error codes, OpenTelemetry names, OpenAPI paths and enums, and deployment configuration keys, each against the document that declares it. |
| `AgentSplice.IntegrationTests` | Host startup and configuration binding, the fake-upstream fixture itself, and the Stage 0 exit criterion that a fake-upstream exchange is representable by the domain model. |
| `AgentSplice.ArchitectureTests` | Module dependency rules, absence of vendor names in the durable core, domain immutability, no static mutable state. |

## Not yet created

`AgentSplice.PerformanceTests` was deliberately not created (ADR 0009). The correctness claim behind
it — that a long stream never routes through the buffered bound — is proven behaviourally in
`AgentSplice.IntegrationTests`, and a wall-clock benchmark on shared CI runners would be either so
loose it proves nothing or so tight it flakes. Conformance and evaluation projects arrive with
Stages 2 and 3, although the Stage 1 contract fixtures become their foundation.

## Fixture families still to come

`docs/TESTING.md` lists the required Stage 1 fixture families. The fake upstream can already produce
every SSE shape they need. The families that depend on a request path — transparent forwarding
assertions, timeout phase attribution, metadata persistence failure, and "no prompt or response in
default logs" — are written by the Stage 1A to 1C slices that introduce those code paths. All four
now exist; what remains for Stage 1C is the administrative API surface and its contract fixtures.
