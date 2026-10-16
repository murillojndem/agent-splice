# Test projects

Test projects follow `docs/TESTING.md`.

## Present

| Project | Covers |
| --- | --- |
| `AgentSplice.TestSupport` | Deterministic fake upstream runtime and repository path helpers. Not a test project itself. |
| `AgentSplice.UnitTests` | Domain rules, timeline absence rules, measurement provenance, configuration validation, privacy defaults. |
| `AgentSplice.ContractTests` | Error codes and types, OpenTelemetry names and instruments, OpenAPI paths and enums, the administrative surface, deployment configuration keys, the image's packaging and health check, the release workflow, the settings the user guides name as variables or `--agentsplice:` arguments and the error codes they name, the license text and `NOTICE`, the third-party notices against the restored dependency graphs, the license and repository declarations and the security policy's reporting link, and the issue forms and chooser, each against the document or file that declares it. |
| `AgentSplice.IntegrationTests` | The request path against the fake upstream — discovery, forwarding, streaming, errors, and timeouts — the administrative API and its authorization, the dashboard's serving rules, persistence, observability, and privacy; host startup and configuration binding; the fake-upstream fixture itself and the Stage 0 exit criterion; the container health probe against a real loopback listener and against the gateway; the Windows package's content-root rule, store location, and publish profile; and the sample client configuration and guide against the gateway's own address and headers. |
| `AgentSplice.ArchitectureTests` | Module dependency rules, absence of vendor names in the durable core, domain immutability, no static mutable state. |

## Not yet created

`AgentSplice.PerformanceTests` was deliberately not created (ADR 0009). The correctness claim behind
it — that a long stream never routes through the buffered bound — is proven behaviourally in
`AgentSplice.IntegrationTests`, and a wall-clock benchmark on shared CI runners would be either so
loose it proves nothing or so tight it flakes. Conformance and evaluation projects arrive with
Stages 2 and 3, although the Stage 1 contract fixtures become their foundation.

## Fixture families

`docs/TESTING.md` lists the required Stage 1 fixture families. The fake upstream produces every SSE
shape they need. The families that depend on a request path — transparent forwarding assertions,
timeout phase attribution, metadata persistence failure, and "no prompt or response in default logs"
— were written by the Stage 1A to 1C slices that introduced those code paths, and Stage 1C added the
administrative API surface with its contract fixtures.
