# Observability specification

## Purpose

Observability is a primary product capability, not an implementation afterthought. AgentSplice must make the client/runtime boundary inspectable without requiring raw sensitive payload retention.

Every displayed or exported value must preserve provenance:

- **measured by AgentSplice**;
- **reported by client**;
- **reported by upstream runtime**;
- **derived from runtime logs**;
- **estimated by a tokenizer or heuristic**;
- **inferred with confidence**;
- **unknown**.

## Trace model

Root activity:

```text
agentsplice.exchange
```

Recommended child activities:

```text
agentsplice.ingress.validate
agentsplice.route.resolve
agentsplice.provider.connect
agentsplice.provider.headers
agentsplice.stream
agentsplice.persistence
agentsplice.replay
agentsplice.conformance.case
agentsplice.evaluation.run
agentsplice.adapter
```

Recommended bounded attributes:

- `gen_ai.operation.name`;
- `gen_ai.request.model` when bounded or normalized;
- `agentsplice.ingress.protocol`;
- `agentsplice.runtime.id`;
- `agentsplice.provider.type`;
- `agentsplice.streaming`;
- `agentsplice.exchange.status`;
- `agentsplice.usage.prompt.source`;
- `agentsplice.usage.completion.source`;
- `agentsplice.cache.classification`;
- `agentsplice.cache.confidence`;
- `agentsplice.adapter.id`;
- `agentsplice.adapter.outcome`;
- `error.type`.

Do not attach prompts, source code, model output, tool arguments, credentials, arbitrary model IDs, request IDs, or file paths as default span attributes.

## Exchange timeline

A completion timeline may contain:

- request accepted;
- request body read;
- validation completed;
- runtime/model resolved;
- structural request summary created;
- upstream connection started;
- upstream connection established;
- upstream response headers received;
- first upstream byte received;
- first SSE event decoded;
- first semantic output event observed;
- client response started (its status and headers flushed);
- first client event flushed;
- first structured tool call observed;
- adapter candidate observed;
- adapter applied/rejected/skipped;
- upstream completed;
- upstream failed, with its classification and bounded detail;
- upstream error reported: the runtime's answer carried the protocol's own error, a top-level `error`;
- client completed, with whether the gateway wrote the relayed answer or the gateway's error envelope;
- client cancelled;
- client response abandoned;
- timeout fired with phase;
- metadata queued;
- metadata persisted or failed.

Timeline events must be sequence-ordered and immutable. Unknown events must not be invented. Runtime prefill completion is recorded only when a reliable provider/runtime signal exists.

### How an exchange ends for the client

Every exchange ends with exactly one client-side boundary, recorded when it happened rather than when
the gateway decided on it (ADR 0025):

| Boundary | Recorded when |
| --- | --- |
| `client_completed` | the last write of the response returned. Its `client.response` detail is `relayed` for the runtime's answer and `gateway_error` for the gateway's own error envelope. |
| `client_cancelled` | the client went away, as far as the server could tell before the response's last write returned. |
| `client_response_abandoned` | AgentSplice abandoned a response it had started: the runtime's answer timed out or was cut off, it crossed a bound the gateway enforces, or the gateway itself faulted. No error envelope reached the client, so the exchange records its failure class and no error code. |

The exchange's completion time is that boundary's, and `exchange.total.duration` runs from acceptance
to it, so a failed or cancelled exchange has a total too and it agrees with
`agentsplice.exchange.duration`. `client_response_started` precedes it on every response the gateway
writes, stamped when the status and headers had been flushed: for a relayed response that is with the
runtime's first body byte, not with the runtime's headers, so a runtime that fails before that byte is
still answered with an error envelope.

Every failure of the request the gateway sent to the runtime is on the timeline. A timeout is `timeout_fired` with its `timeout.phase` —
`Connect`, `ResponseHeaders`, `FirstByte`, `IdleStream`, or `Total` — wherever it elapsed. `FirstByte`
is a stream whose runtime sent its headers and then no byte of its body in time; it always happens
before the client's status is committed, so it is always answered with a `504`. `IdleStream` is a gap
between two reads after the first byte, so it always abandons a response that had started
(ADR 0028). A record made before ADR 0028 names `IdleStream` for the wait before the first byte too;
its timeline has no `first_upstream_byte`. Any other failure is
`upstream_failed`, whose `upstream.failure` is the classification (`Unreachable`,
`AuthenticationRejected`, `InvalidResponse`, or `ResponseTooLarge`) and whose other details are the
provider's own bounded tokens, such as `upstream.stream: connection.lost`,
`upstream.completion: body.truncated`, or `upstream.socket_error: ConnectionRefused`. A client that
left records neither: `client_cancelled` says so. Discovery is not that request: a runtime whose model list could not be read while a completion was being routed is recorded by the exchange's failure class, `runtime_unavailable`, with no `upstream_failed` boundary, and `/api/v1/health/runtimes` says how its last consultation went.

### Where each streaming boundary is stamped

These boundaries describe the first moments of a streamed response, and each is a different instant. Sharing one clock reading between them — which the first Stage 1B implementation did — makes time to first byte, flush latency, decode cost, and time to first token all exactly zero, which is indistinguishable from not measuring at all (ADR 0010).

| Boundary | The operation it names |
| --- | --- |
| first upstream byte | the upstream read returned a positive byte count. Never a reading taken before that read: it would date the moment AgentSplice began waiting. |
| client response started | the client's status and headers, committed with that first byte, had been flushed. |
| first client event flushed | the write carrying the bytes that completed the first event a client **dispatches** finished flushing. |
| first SSE event decoded | the frame reader handed out its first complete frame. A comment or keepalive counts. |
| first semantic output event | the protocol interpreter classified a frame as carrying model output: content, reasoning in `reasoning_content` or `reasoning`, or a tool call. A role announcement is not output. |

A frame a conforming client dispatches no event for — a comment, a bare `id`, a `retry` directive, an `event` name with no payload — may set the decoded boundary and must never set the client-event boundary, because dating first delivery from one would report a response as having reached the client before it carried anything. A `data` field with an empty value does dispatch and does count: its buffer holds a line feed rather than nothing.

Because the relay writes before it decodes, the client-flush boundary is chronologically earlier than the decode boundary it was learned from. Boundaries are appended in the order they occurred, not the order they were learned, so the timeline never runs backwards. Every derived duration depends on that: a negative interval is dropped rather than reported, so an out-of-order boundary does not produce a wrong number — it makes a whole phase disappear.

On the buffered path there is no decode or flush boundary, and the first upstream byte is stamped inside the body reader's first-read callback. Read afterwards, it would name the moment the body finished and file it as the moment it started.

Upstream completion for a streamed response is dated from the protocol terminator when one is observed, not from the transport ending. A runtime that sends `[DONE]` and holds its connection open would otherwise stretch the upstream duration, and the generation window derived from it, across a stall that produced nothing.

## Latency phases

Always distinguish:

- gateway queue time;
- request parsing and validation;
- routing/model resolution;
- upstream connection time;
- time to upstream response headers;
- time to first upstream byte;
- time to first semantic output event;
- time to first client event;
- prompt-processing duration where observable;
- generation duration where observable;
- adapter buffering delay;
- persistence delay;
- total wall-clock time.

Upstream connection time is measured by taking over connection establishment in the provider's handler, because it happens below the request path and is invisible otherwise. It is recorded as two boundaries and derived like every other phase, and it is **absent for a request served by a pooled connection** — no connection was established, which is not the same as one that took no time. Without this phase, a runtime slow to accept connections and a runtime slow to think produce the same number and send an operator to different places.

Three phases have no measurement name at all, because nothing in Stage 1 can produce one and a name nothing produces is worse than an absence. Gateway queue time needs a host timing feature Kestrel does not portably expose, and request acceptance is the earliest instant AgentSplice can read. Adapter buffering delay has no adapters to delay anything. Prompt-processing duration has no observable end: nothing in an OpenAI-compatible stream marks the moment prefill finished, so the only available interval is time to first token — which contains the prompt, the queue, and the network together, and must never be published under a prompt-processing name. It becomes derivable with runtime-log evidence in Stage 2E. Persistence delay arrives with Stage 1C.

The dashboard must not label prompt-processing throughput as generation throughput. A high prompt-token rate does not imply low end-to-end latency.

## Metrics

Proposed instruments:

```text
agentsplice.exchanges
agentsplice.active_exchanges
agentsplice.exchange.duration
agentsplice.gateway.duration
agentsplice.upstream.duration
agentsplice.time_to_headers
agentsplice.time_to_first_byte
agentsplice.time_to_first_semantic_event
agentsplice.time_to_first_client_event
agentsplice.stream.events
agentsplice.stream.bytes
agentsplice.prompt.tokens
agentsplice.completion.tokens
agentsplice.prompt.tokens_per_second
agentsplice.generation.tokens_per_second
agentsplice.adapter.invocations
agentsplice.adapter.failures
agentsplice.runtime.health
agentsplice.model_discovery.duration
agentsplice.persistence.failures
agentsplice.replay.duration
agentsplice.conformance.cases
agentsplice.evaluation.runs
```

Dimensions must be bounded to normalized runtime ID, provider type, protocol, adapter ID, streaming boolean, status class, runtime outcome, error class, suite ID, and scenario ID. Raw request IDs and arbitrary model identifiers must not become metric labels.

### Live instruments

These are emitted today. The rest of the list above is declared but not yet emitted.

- `agentsplice.exchanges`
- `agentsplice.active_exchanges`
- `agentsplice.exchange.duration`
- `agentsplice.upstream.duration`
- `agentsplice.time_to_headers`
- `agentsplice.prompt.tokens`
- `agentsplice.completion.tokens`
- `agentsplice.model_discovery.duration`
- `agentsplice.time_to_first_byte`
- `agentsplice.time_to_first_semantic_event`
- `agentsplice.time_to_first_client_event`
- `agentsplice.stream.events`
- `agentsplice.stream.bytes`
- `agentsplice.generation.tokens_per_second`
- `agentsplice.persistence.failures`

The streaming instruments record only what a streamed exchange observed. A buffered exchange contributes no data point rather than a zero: in a series where the value means something, a zero reads as "this happened, and it was none".

Token instruments record only what a runtime actually reported. Absent usage produces no data point rather than a zero. They carry no provenance attribute because in this stage every count is the runtime's own; a stage that adds an estimated count adds the attribute with it.

`agentsplice.generation.tokens_per_second` is **derived**, not reported: the completion tokens the runtime reported, divided by the **observed decode window** AgentSplice measured — from the first event carrying model output, reasoning included, to upstream completion. It therefore excludes the first token's own decode latency while still counting that token. The bias is negligible over a long generation and material over a very short one. It is stated here rather than corrected, because dividing by one fewer token would invent a number the runtime never reported. It is not the runtime's own tokens per second, which counts its own window.

A derived value carries the provenance of its weakest input, so this one is `upstream_reported` — the rule that keeps an estimate from passing as a measurement (ADR 0009 decision 7). That makes the provenance a trust rank here rather than a statement that the runtime reported the number, and the dashboard says "derived" beside it (ADR 0027).

Reasoning counts as output. A runtime that streams reasoning in its own delta field, and counts it in `completion_tokens`, would otherwise have its window start at the first answer token, inflating the figure by however much of the generation was reasoning.

The window closes at the protocol terminator, not at transport end, so a runtime that stops generating and keeps its connection open cannot deflate this figure with idle time (ADR 0010).

`agentsplice.stream.bytes` and `agentsplice.stream.events` count different things and can legitimately disagree. Bytes count everything forwarded to the client, counted when each write returned — that is, when the server accepted them. An abandoned response ends in a reset, and a client that left mid-stream took the last of them with it, so for an abandoned or cancelled stream the count is an upper bound. A stream the runtime failed before its first byte was answered with an error envelope instead, and contributes no data point to either. The record's `stream.client.bytes` measurement follows the same rule, on the buffered path too: it is what the server accepted, recorded once the write returned, and absent when the server reported the client gone before then. A write returns when the server has taken the bytes, which for a buffered answer can be all of it before the client has read any — Kestrel took a 32 MiB answer whole within milliseconds on Windows and on CI's Linux runners — so the count is what was handed to the client's connection, an upper bound on what arrived, and `client_completed` on that path means the same. Events count what was interpreted, and interpretation stops at the protocol terminator — so bytes a runtime coalesced behind its own `[DONE]` inside one network read are delivered and counted as bytes, but contribute no events. Interpreting them would extend a response the protocol had already declared finished. The same holds for bytes behind a per-event bound violation in that read: the terminator that preceded it still ends the exchange normally (ADR 0011).

`agentsplice.prompt.tokens_per_second` is **absent by design, not deferred**. Nothing AgentSplice can observe marks the end of prompt processing, so the only interval available is time to first token — which measures the prompt, the queue, and the network together. Publishing that under a prompt-throughput name is exactly the conflation this document forbids. It becomes derivable only with runtime-log evidence (Stage 2E).

### Live dimensions

- `agentsplice.ingress.protocol`
- `agentsplice.runtime.id`
- `agentsplice.provider.type`
- `agentsplice.streaming`
- `agentsplice.exchange.status`
- `agentsplice.upstream.status_class`
- `agentsplice.upstream.outcome`
- `error.type`
- `agentsplice.stream.termination`
- `agentsplice.persistence.failure_reason`

`agentsplice.persistence.failure_reason` is attached only to `agentsplice.persistence.failures` and has two values. `QueueSaturated` means the gateway produced evidence faster than the store accepted it and the fix is capacity; `WriteFailed` means the store rejected what it was given and the fix is the store. A single undifferentiated failure count would send an operator adding queue capacity to a database that is refusing every write.

`agentsplice.upstream.status_class` carries the coarse class of the runtime's status — `2xx`, `3xx`, `4xx`, `5xx`, or `other` — not the client's: a stream the runtime answered `200` and then failed before its first byte is `2xx` there, and `Failed` with the gateway's `504` everywhere else. A relayed upstream 500 is a completed transport cycle with no AgentSplice failure, so classifying on the absence of an error would count it as a success; the outcome below is what success is classified from.

`agentsplice.upstream.outcome` is what the runtime's success and failure are classified from (ADR 0026). It is attached to a completed exchange that heard a response, and has three values: `Success` for a 2xx whose answer was read and carried no error, `ErrorStatus` for any status outside 2xx, redirects included, and `ErrorReported` for a 2xx whose answer carried the protocol's own error — LM Studio reported a chat template it could not render inside a `200` stream, which the status class alone counted as a success. A 2xx whose answer could not be read carries none: a success is a claim about the answer. A failed or cancelled exchange carries no outcome: its `error.type` or its status already says how it ended, and the runtime's answer never completed to be judged. The exchange span carries the same attribute, and its status is `Error` when the outcome is an error as well as when AgentSplice failed; `error.type` stays AgentSplice's own. That departs from OpenTelemetry's HTTP convention, which leaves a server span unset for a 4xx and expects `error.type` beside `Error`: the exchange span describes the exchange, not the HTTP request, and a runtime's failure is the failure an operator searching for one needs, while `error.type` is kept for AgentSplice's own so the two stay apart.

`agentsplice.stream.termination` is attached only to exchanges that actually streamed, so adding it costs the existing series no cardinality at all. Its value set is closed: the eleven members of `StreamTermination`, among them `UpstreamError`, a stream in which the runtime reported an error and which then ended by itself.

There is deliberately no model dimension. A model identifier is client-supplied and unbounded, so using it as a label would let one caller multiply the cardinality of every series without limit.

### Tracing

Spans are emitted through `System.Diagnostics.ActivitySource`, and the OpenTelemetry SDK is the only subscriber (ADR 0014). It runs whether or not anything is exported, because a source nobody listens to makes `StartActivity` return null — every span would be absent and `x-agentsplice-trace-id` could never be populated. The SDK replaced a hand-registered `ActivityListener` rather than joining it, and an architecture test fails if one returns. The W3C identifier format is forced explicitly, and every exchange is sampled: `AlwaysOnSampler`, not the SDK's parent-based default, so a client that propagates a trace context marked unsampled cannot discard the spans for its own exchange.

All four Stage 1 sources are live: `agentsplice.exchange`, `agentsplice.provider.request`, `agentsplice.stream`, and `agentsplice.persistence`. The provider span covers opening the upstream response alone; the stream span covers the transfer that follows. Keeping them apart is what separates "the runtime took a long time to answer" from "the runtime produced a long answer". The persistence span covers one write batch, which is an interval no exchange span can contain: by the time it runs the client response is long finished.

The SDK subscribes to a source only once something writes to it, because a source nothing writes to is a permanently empty panel that reads as a capability which produced nothing.

## Structured logs

Every event the gateway logs on purpose has a stable identifier, which an alert rule or a log query can
match while the message is reworded (`GatewayEventIds`). The thousands say which part of the system
spoke: 1000 the request path, 1100 routing and discovery, 1200 hosting, 1300 persistence and retention.

| Event | Level | Logged when |
|---|---|---|
| `1001 ExchangeFaulted` | Error | A completion request faulted in a way the gateway did not anticipate. |
| `1002 EvidenceRecordingFailed` | Error | Recording an exchange's evidence failed; the client response is unaffected. |
| `1003 InstrumentationFailed` | Error | Emitting spans or metrics failed; the client response is unaffected. |
| `1101 RuntimeDiscoveryFailed` | Warning | A runtime's model catalogue could not be refreshed. |
| `1102 ModelListFailed` | Error | Building the model list `/v1/models` serves failed. |
| `1103 RuntimeCredentialMissing` | Warning | A runtime names an API key variable that is unset or blank. |
| `1201 UnhandledPipelineFault` | Error | A fault escaped the request pipeline entirely. |
| `1202 TelemetryExportEnabled` | Information | Export is on; the message names the transport and the destination. |
| `1203 DashboardServed` | Information | A dashboard build is served; the message names the directory. |
| `1204 DashboardBuildMissing` | Warning | The dashboard is enabled and no build was found where the message says. |
| `1205 InferenceReachableBeyondLoopback` | Warning | Once at startup, when the gateway listens beyond loopback or trusts forwarded headers: `/v1` authenticates no one, and the token protects `/api/v1` only (ADR 0027). |
| `1301 MetadataQueueSaturated` | Warning | The metadata queue was full, so an exchange's evidence was dropped. |
| `1302 MetadataPersistenceFailed` | Error | The store rejected a batch, and the exchanges in it were lost. |
| `1303 RetentionSweepCompleted` | Information | A retention sweep finished; the message says what it removed. |
| `1304 RetentionSweepFailed` | Error | A retention sweep could not complete. |
| `1305 MetadataStoreOpened` | Information | The store is open and current; the message names its file. |

`1301` and `1302` are counted too, since the gateway started, in `persistenceFailures` on
`GET /api/v1/system`, and the dashboard's overview says when either is above zero: with export off, the
`agentsplice.persistence.failures` metric reaches no backend, and a store refusing every write would
otherwise look like a quiet afternoon (ADR 0027).

Other components log under their own categories without these identifiers. The one an operator is
likely to need is ASP.NET Core's host filter, whose `NoAllowedHostMatched` line names a host the gateway
refused; the shipped `appsettings.json` raises `Microsoft.AspNetCore.HostFiltering` to Information so
it is visible (`docs/INSTALLATION.md`, "Host names").

Default logs include structural metadata only. Body logging is not a supported shortcut for trace capture.

The dashboard announces itself once at startup, from `AgentSplice.Api.Hosting.DashboardHosting`:
`1203 DashboardServed` (Information) when a build is served, naming the directory, and
`1204 DashboardBuildMissing` (Warning) when the dashboard is enabled but no build was found, naming
the absolute path that was checked. A disabled dashboard logs neither (ADR 0015).

The metadata store does the same once it is open and migrated, from
`AgentSplice.Infrastructure.Persistence.MetadataStoreInitializer`: `1305 MetadataStoreOpened`
(Information), naming the file. A relative `Data Source` is resolved against the content root, so this
is where an operator finds out which file that was — beside the executable in the Windows and Linux
packages, `/data/agentsplice.db` in the image (ADR 0017, ADR 0021). It carries the data source only, never the connection
string, which may hold a password. A deployment that retains nothing logs no store at all.

## Runtime-log ingestion

A later adapter may ingest LM Studio, llama.cpp, Ollama, or backend logs to enrich:

- prompt-processing progress;
- token throughput;
- speculative/MTP acceptance;
- slot selection;
- cache/checkpoint evidence;
- backend fallback warnings;
- memory observations.

Log ingestion is optional, version-specific, and isolated from request correctness. Parser failures must not alter proxy behavior.

## Dashboard principles

- show total wall-clock time first;
- render latency as a waterfall;
- distinguish client, gateway, network/runtime, and downstream phases;
- label every token and throughput source;
- distinguish cold and warm runs;
- display unknown instead of zero when evidence is absent;
- show adapter activity separately from transparent forwarding;
- avoid content exposure by default;
- link traces to replay, conformance, and regression evidence.

## OpenTelemetry export

AgentSplice should follow current OpenTelemetry GenAI semantic conventions where they cleanly map, using AgentSplice-prefixed attributes for concepts not standardized. Semantic-convention version changes must be reviewed and tested rather than adopted silently.

### Turning it on

Traces and metrics are exported over OTLP when, and only when, an operator turns export on (ADR 0014):

```yaml
agentsplice:
  telemetry:
    otlp:
      enabled: true
      endpoint: http://127.0.0.1:4318   # the receiver's base address
      protocol: HttpProtobuf            # default; or Grpc, conventionally on 4317
```

- **Off by default, and never defaulted to a destination.** Enabling export without an endpoint fails startup rather than sending to the SDK's `localhost` default. `/api/v1/system` reports `telemetryExportEnabled`.
- **The endpoint is the receiver's base address.** For `HttpProtobuf`, AgentSplice appends `v1/traces` and `v1/metrics` after any path it already has, so a collector behind a proxy at `/otlp` receives spans at `/otlp/v1/traces`. For `Grpc` it carries no path. Startup refuses an endpoint with credentials, a query, a fragment, or a signal path in place of the base address.
- **`OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_PROTOCOL` have no effect.** A variable left over from another tool is not consent to export a description of every exchange. The SDK's other standard variables still apply: `OTEL_EXPORTER_OTLP_HEADERS` is how a collector credential is supplied without entering AgentSplice's configuration or log, and `OTEL_EXPORTER_OTLP_TIMEOUT` bounds each export.
- **Starting export is logged once**, as event `1202 TelemetryExportEnabled`, naming the transport and destination.

### What is exported

- The four live activity sources and the `AgentSplice` meter — the instruments and bounded dimensions listed above, which the spans carry as attributes, plus `agentsplice.persistence.batch_size` on the persistence span: a count of exchanges in one write, never which ones. No prompt, model output, tool argument, model identifier, request identifier, or credential; an integration test searches every exported payload for each of them.
- Resource attributes `service.name=agentsplice`, `service.version` (the build's informational version), and a generated `service.instance.id`.
- **Not logs.** The log pipeline is not routed through OpenTelemetry. **Not framework spans.** ASP.NET Core and `HttpClient` instrumentation are not enabled, so health probes, administrative requests, and requests for the dashboard's files produce no spans. That includes the container image's own health check (ADR 0016), which asks `/health/live` every thirty seconds: the gateway logs nothing for it at the shipped levels and exports nothing for it. What the probe reported — one line per run, with the status and how long the answer took — is kept by Docker, in `docker inspect`'s `State.Health.Log`, not by AgentSplice.

Spans leave on the SDK's five-second batch schedule; metrics on its default sixty-second interval.

### When the collector is unreachable

Export runs on the SDK's own threads with a bounded queue, and it never affects an exchange: a collector that is down, slow, or refusing costs a failed export and nothing else. The price is that the failure is quiet. The SDK reports exporter errors through its `OpenTelemetry-Exporter-OpenTelemetryProtocol` event source, not through the gateway log, so a failing export is visible only with SDK self-diagnostics enabled. Surfacing it in the gateway's own log is follow-up work.

### Known limitation: the exchange span's parent

ASP.NET Core creates an activity for every request, and `agentsplice.exchange` starts as its child. That activity is not exported, so a backend shows the exchange span with a parent it never received. The trace identifier is unaffected — `x-agentsplice-trace-id` finds the exchange in the backend — but a client's propagated span links to it only through the missing one.

### Local collector

The Compose file carries an optional collector that prints what it receives:

```powershell
# .env: AGENTSPLICE__TELEMETRY__OTLP__ENABLED=true
docker compose --profile otel up
docker compose logs -f otel-collector
```

It listens on the Compose network only and publishes no port on the host. `config/otel-collector.yaml` is a starting point: add an exporter for a backend to keep the data.
