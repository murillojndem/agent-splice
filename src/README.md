# Source projects

Bootstrap only the projects required by the active roadmap stage in `docs/ARCHITECTURE.md` and `CLAUDE.md`.

## Present

| Project | Stage 0 contents |
| --- | --- |
| `AgentSplice.Domain` | Exchanges, timelines, observations, measurements, provenance, identifiers. References nothing. |
| `AgentSplice.Application` | Validated configuration and stable error codes. References Domain. |
| `AgentSplice.Infrastructure` | Configuration binding with startup validation. |
| `AgentSplice.Protocols.OpenAI` | Boundary only. Ingress DTOs and the incremental SSE reader/writer arrive with Stage 1A/1B. |
| `AgentSplice.Providers.LmStudio` | Boundary only. The provider adapter arrives with Stage 1A. |
| `AgentSplice.Observability` | Normative OpenTelemetry names. Instruments and exporters arrive with Stage 1. |
| `AgentSplice.Api` | Composition root. Boots and validates configuration; exposes no HTTP surface in Stage 0. |

The two boundary-only projects exist so that `AgentSplice.ArchitectureTests` can assert no
vendor-specific type has leaked into the durable core. They are deliberately empty rather than filled
with speculative interfaces.

## Not yet created

Replay, conformance orchestration, evaluation workers, Anthropic translation, and vendor-specific
adapter projects are created by the roadmap task that reaches those stages, not in advance.

Do not place implementation directly in this directory.
