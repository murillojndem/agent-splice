# Source projects

Bootstrap only the projects required by the active roadmap stage in `docs/ARCHITECTURE.md` and `CLAUDE.md`.

## Present

| Project | Contents through Stage 1B |
| --- | --- |
| `AgentSplice.Domain` | Exchanges, timelines, observations, measurements, provenance, identifiers, stream terminations. References nothing. |
| `AgentSplice.Application` | The request path: model resolution and the discovery cache, the chat-completion orchestrator, the streaming relay and its pump, the incremental SSE frame reader, the protocol and runtime ports, header policy, validated configuration, and stable error codes. References Domain. |
| `AgentSplice.Infrastructure` | Configuration binding with startup validation, request-path composition, credential resolution from the environment, and the metadata store: the EF Core SQLite context and migrations, the persistence rows and their mapper, the bounded queue sink, and the background writer. The only project that references EF Core, which an architecture test enforces. |
| `AgentSplice.Protocols.OpenAI` | The OpenAI-compatible protocol: request scanning and alias substitution over raw bytes, response and stream-event interpretation, the error envelope, the model-list writer, and stream media-type matching. |
| `AgentSplice.Providers.LmStudio` | The LM Studio provider: buffered and streaming upstream calls, bounded body reading, per-phase timeout budgets, connection timing, and transport-failure classification. |
| `AgentSplice.Observability` | Normative telemetry names, the exchange/provider/stream/persistence activity sources and their listener, and the live instruments. No OpenTelemetry SDK or exporter until Stage 1D. |
| `AgentSplice.Api` | Composition root and transport glue: the `/v1/models` and `/v1/chat/completions` endpoints, correlation headers, the client response sink, request-body reading, and the concurrency policy. Carries no orchestration logic, which an architecture test enforces. |

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
