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
agentsplice.stream.receive
agentsplice.stream.forward
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
- upstream request headers sent;
- upstream response headers received;
- first upstream byte received;
- first SSE event decoded;
- first semantic output event observed;
- first client event flushed;
- first structured tool call observed;
- adapter candidate observed;
- adapter applied/rejected/skipped;
- upstream completed;
- client completed;
- client cancelled;
- timeout fired with phase;
- metadata queued;
- metadata persisted or failed.

Timeline events must be sequence-ordered and immutable. Unknown events must not be invented. Runtime prefill completion is recorded only when a reliable provider/runtime signal exists.

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

Dimensions must be bounded to normalized runtime ID, provider type, protocol, adapter ID, streaming boolean, status class, error class, suite ID, and scenario ID. Raw request IDs and arbitrary model identifiers must not become metric labels.

### Stage 1A instruments

These are live. The rest of the list above is declared but not yet emitted.

- `agentsplice.exchanges`
- `agentsplice.active_exchanges`
- `agentsplice.exchange.duration`
- `agentsplice.upstream.duration`
- `agentsplice.time_to_headers`
- `agentsplice.prompt.tokens`
- `agentsplice.completion.tokens`
- `agentsplice.model_discovery.duration`

Every streaming instrument, the first-byte and first-event timings, and both throughput instruments are deliberately absent. A non-streamed exchange offers no boundary to measure them against, and emitting a zero would be worse than emitting nothing: in a metric stream where Stage 1B will mean something by the value, a zero reads as "this happened, and it was none".

Token instruments record only what a runtime actually reported. Absent usage produces no data point rather than a zero.

### Stage 1A dimensions

- `agentsplice.ingress.protocol`
- `agentsplice.runtime.id`
- `agentsplice.provider.type`
- `agentsplice.streaming`
- `agentsplice.exchange.status`
- `agentsplice.upstream.status_class`
- `error.type`

`agentsplice.upstream.status_class` carries the coarse class — `2xx`, `4xx`, `5xx` — and is what success and failure are classified from. A relayed upstream 500 is a completed transport cycle with no AgentSplice failure, so classifying on the absence of an error would count it as a success.

There is deliberately no model dimension. A model identifier is client-supplied and unbounded, so using it as a label would let one caller multiply the cardinality of every series without limit.

### Stage 1A tracing

Spans are emitted through `System.Diagnostics.ActivitySource`; no OpenTelemetry SDK is referenced, and an architecture test enforces that. Because nothing else subscribes to the `agentsplice.*` sources, AgentSplice registers its own `ActivityListener` and forces the W3C identifier format — without it `StartActivity` returns null, every span is absent, and `x-agentsplice-trace-id` can never be populated. Stage 1B replaces that listener with the SDK and must not run both.

## Structured logs

Use stable event IDs and templates. Examples:

- exchange accepted;
- model resolved;
- upstream request started;
- upstream response headers received;
- stream terminated;
- observation recorded;
- adapter applied;
- adapter rejected;
- replay started/completed;
- conformance case failed;
- persistence failed;
- runtime discovery failed.

Default logs include structural metadata only. Body logging is not a supported shortcut for trace capture.

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
