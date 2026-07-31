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

### Where each streaming boundary is stamped

Four boundaries describe the first moments of a streamed response, and they are four different instants. Sharing one clock reading between them — which the first Stage 1B implementation did — makes time to first byte, flush latency, decode cost, and time to first token all exactly zero, which is indistinguishable from not measuring at all (ADR 0010).

| Boundary | The operation it names |
| --- | --- |
| first upstream byte | the upstream read returned a positive byte count. Never a reading taken before that read: it would date the moment AgentSplice began waiting. |
| first client event flushed | the write carrying the bytes that completed the first event a client **dispatches** finished flushing. |
| first SSE event decoded | the frame reader handed out its first complete frame. A comment or keepalive counts. |
| first semantic output event | the protocol interpreter classified a frame as carrying model output. |

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

Dimensions must be bounded to normalized runtime ID, provider type, protocol, adapter ID, streaming boolean, status class, error class, suite ID, and scenario ID. Raw request IDs and arbitrary model identifiers must not become metric labels.

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

The streaming instruments record only what a streamed exchange observed. A buffered exchange contributes no data point rather than a zero: in a series where the value means something, a zero reads as "this happened, and it was none".

Token instruments record only what a runtime actually reported. Absent usage produces no data point rather than a zero.

`agentsplice.generation.tokens_per_second` is measured over the **observed decode window** — from the first event carrying model output to upstream completion — and therefore excludes the first token's own decode latency while still counting that token. The bias is negligible over a long generation and material over a very short one. It is stated here rather than corrected, because dividing by one fewer token would invent a number the runtime never reported.

The window closes at the protocol terminator, not at transport end, so a runtime that stops generating and keeps its connection open cannot deflate this figure with idle time (ADR 0010).

`agentsplice.stream.bytes` and `agentsplice.stream.events` count different things and can legitimately disagree. Bytes count everything forwarded to the client. Events count what was interpreted, and interpretation stops at the protocol terminator — so bytes a runtime coalesced behind its own `[DONE]` inside one network read are delivered and counted as bytes, but contribute no events. Interpreting them would extend a response the protocol had already declared finished. The same holds for bytes behind a per-event bound violation in that read: the terminator that preceded it still ends the exchange normally (ADR 0011).

`agentsplice.prompt.tokens_per_second` is **absent by design, not deferred**. Nothing AgentSplice can observe marks the end of prompt processing, so the only interval available is time to first token — which measures the prompt, the queue, and the network together. Publishing that under a prompt-throughput name is exactly the conflation this document forbids. It becomes derivable only with runtime-log evidence (Stage 2E).

### Live dimensions

- `agentsplice.ingress.protocol`
- `agentsplice.runtime.id`
- `agentsplice.provider.type`
- `agentsplice.streaming`
- `agentsplice.exchange.status`
- `agentsplice.upstream.status_class`
- `error.type`
- `agentsplice.stream.termination`

`agentsplice.upstream.status_class` carries the coarse class — `2xx`, `4xx`, `5xx` — and is what success and failure are classified from. A relayed upstream 500 is a completed transport cycle with no AgentSplice failure, so classifying on the absence of an error would count it as a success.

`agentsplice.stream.termination` is attached only to exchanges that actually streamed, so adding it costs the existing series no cardinality at all. Its value set is closed: the ten members of `StreamTermination`.

There is deliberately no model dimension. A model identifier is client-supplied and unbounded, so using it as a label would let one caller multiply the cardinality of every series without limit.

### Tracing

Spans are emitted through `System.Diagnostics.ActivitySource`; no OpenTelemetry SDK is referenced, and an architecture test enforces that. Because nothing else subscribes to the `agentsplice.*` sources, AgentSplice registers its own `ActivityListener` and forces the W3C identifier format — without it `StartActivity` returns null, every span is absent, and `x-agentsplice-trace-id` can never be populated. Stage 1C replaces that listener with the SDK and must not run both.

Three sources are live: `agentsplice.exchange`, `agentsplice.provider.request`, and `agentsplice.stream`. The provider span covers opening the upstream response alone; the stream span covers the transfer that follows. Keeping them apart is what separates "the runtime took a long time to answer" from "the runtime produced a long answer". `agentsplice.persistence` is declared but has no producer until Stage 1C, and the listener does not subscribe to it: a source nothing writes to is a permanently empty panel that reads as a capability which produced nothing.

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
