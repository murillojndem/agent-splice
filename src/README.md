# Source projects

Bootstrap only the projects required by the active roadmap stage in `docs/ARCHITECTURE.md` and `CLAUDE.md`.

## Present

| Project | Contents through Stage 1B |
| --- | --- |
| `AgentSplice.Domain` | Exchanges, timelines, observations, measurements, provenance, identifiers, stream terminations. References nothing. |
| `AgentSplice.Application` | The request path: model resolution and the discovery cache, the chat-completion orchestrator, the streaming relay and its pump, the incremental SSE frame reader, the protocol and runtime ports, header policy, validated configuration, and stable error codes. References Domain. |
| `AgentSplice.Infrastructure` | Configuration binding with startup validation, request-path composition, credential resolution from the environment, and the metadata store: the EF Core SQLite context and migrations, the persistence rows and their mapper, the bounded queue sink, the background writer, and the resolution of a relative store path against the content root (ADR 0017). The only project that references EF Core, which an architecture test enforces. |
| `AgentSplice.Protocols.OpenAI` | The OpenAI-compatible protocol: request scanning and alias substitution over raw bytes, response and stream-event interpretation, the error envelope, the model-list writer, and stream media-type matching. |
| `AgentSplice.Providers.LmStudio` | The LM Studio provider: buffered and streaming upstream calls, bounded body reading, per-phase timeout budgets, connection timing, and transport-failure classification. |
| `AgentSplice.Observability` | Normative telemetry names, the exchange/provider/stream/persistence activity sources, the live instruments, and the OpenTelemetry SDK that subscribes to them, with opt-in OTLP export. The only project that references OpenTelemetry, which an architecture test enforces. |
| `AgentSplice.Api` | Composition root and transport glue: the `/v1/models` and `/v1/chat/completions` endpoints, correlation headers, the client response sink, request-body reading, the concurrency policy, the hosting of the dashboard build at `/dashboard/` with its security headers (ADR 0015), and the content-root rule that lets the Windows package run from any folder (ADR 0017). Carries no orchestration logic, which an architecture test enforces. |
| `AgentSplice.HealthProbe` | The container image's health check (ADR 0016): one `GET` against the loopback liveness URL it is given, exiting 0 on any 2xx and 1 otherwise. Not part of the gateway — it references only the base class library and nothing references it, which an architecture test enforces. |

The protocol and provider modules stay separate from Domain and Application so that
`AgentSplice.ArchitectureTests` can assert no vendor-specific type has leaked into the durable core,
and that Application never touches `System.Net.Http` or `Microsoft.AspNetCore`.

Persistence writes exchange metadata only. `IExchangeRecordSink` resolves to the queued sink when a
store is configured and to a discarding one when `agentsplice:persistence:mode` is `None`, which is a
supported deployment rather than a degraded one (FR-DATA-001). Nothing on the request path waits for
the database: the sink calls `TryWrite` and returns, and a background service does the writing.

The store's row types are deliberately separate from the domain records. Mapping `CompletionExchange`
directly would require EF Core conventions inside `AgentSplice.Domain`, and it would leave no way to
record a request that was refused before its envelope was read — which has no `CompletionExchange` at
all, and is exactly the request an operator needs to see.

## Not yet created

Replay, conformance orchestration, evaluation workers, Anthropic translation, and vendor-specific
adapter projects are created by the roadmap task that reaches those stages, not in advance.

Do not place implementation directly in this directory.
