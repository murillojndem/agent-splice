# Conformance specification

## Purpose

The conformance subsystem determines whether a client, gateway, runtime, model profile, and protocol combination behaves according to an explicit contract. HTTP 200 alone is never sufficient evidence of support.

Conformance is a durable product capability. It remains useful even when all known compatibility defects are fixed because protocol implementations, templates, runtimes, model families, quantizations, and versions continue to change.

## Conformance layers

A conformance result must identify the layer under test:

1. **Ingress API conformance** — whether AgentSplice accepts and validates a documented client request.
2. **Forwarding conformance** — whether fields and ordering are preserved or intentionally transformed.
3. **Upstream runtime conformance** — whether the runtime accepts the forwarded request and returns a valid response.
4. **Streaming conformance** — whether SSE framing and semantic event order are valid.
5. **Tool-call conformance** — whether tool declarations, selections, arguments, IDs, and results remain structurally valid.
6. **Translation conformance** — whether mapping between protocols preserves supported semantics.
7. **Cancellation and timeout conformance** — whether termination propagates predictably.
8. **Usage and timing conformance** — whether reported values are internally consistent and correctly attributed.
9. **Replay conformance** — whether an artifact can be reproduced under the declared replay mode.
10. **Agent-loop conformance** — whether the interaction remains valid over multiple model/tool turns.

## Initial suites

### OpenAI Chat Completions suite

- minimal non-streaming request;
- minimal streaming request;
- ordered system, user, assistant, and tool messages;
- unknown extension fields;
- tools with nested JSON Schema;
- `tool_choice` values;
- usage terminal chunk;
- finish reason mapping;
- model-not-found error;
- invalid request error;
- client cancellation;
- upstream timeout;
- malformed upstream JSON;
- malformed SSE event;
- premature stream termination.

### SSE suite

- event split across arbitrary byte boundaries;
- multiple events in one network read;
- multiline `data:` fields;
- comments and keepalive events;
- blank lines and CRLF/LF variants;
- UTF-8 characters split across reads;
- terminal `[DONE]` handling;
- usage-only terminal event;
- invalid JSON payload;
- duplicate terminal event;
- connection close without terminal event;
- bounded buffering enforcement.

### Tool-call suite

- one native structured call;
- multiple ordered calls;
- nested arguments;
- empty object arguments;
- Unicode arguments;
- invalid argument JSON;
- unknown tool name;
- missing tool-call ID;
- duplicate tool-call IDs;
- streaming argument fragments;
- parallel calls where supported;
- tool result continuity;
- ordinary prose that resembles a call;
- profile-gated text-encoded call;
- ambiguous text candidate;
- malformed candidate;
- schema validation success and failure.

### Anthropic Messages suite — later stage

- system content;
- content block lifecycle;
- tool-use blocks;
- tool-result blocks;
- streaming event order;
- stop reason mapping;
- translation-loss reporting;
- unsupported vendor beta behavior.

### Cache evidence suite

This suite does not claim direct access to a runtime's private KV cache. It classifies observable evidence:

- identical stable prefix with short suffix;
- changed system prompt;
- changed tool order;
- changed request serialization only;
- cold runtime versus warm runtime;
- slot reuse where observable;
- runtime-log evidence where an adapter exists;
- timing and processed-token deltas.

Results must be labeled `probable_hit`, `partial_hit`, `probable_miss`, `cold`, or `unknown`, with confidence and evidence.

## Result model

Each case produces:

- suite ID and version;
- case ID and version;
- environment snapshot ID;
- client/protocol/runtime/model/profile identifiers;
- start and completion time;
- pass, fail, skipped, unsupported, or inconclusive status;
- expected and observed behavior;
- failure layer;
- safe evidence references;
- transformations applied;
- replay artifact reference when retained;
- implementation and fixture versions.

## Support declaration policy

A compatibility-matrix row may be marked:

- **Verified** only when required suites pass under a dated environment snapshot;
- **Partially verified** when explicitly listed cases fail or are unsupported;
- **Experimental** when fixtures exist but evidence is insufficient;
- **Broken** when a required case fails reproducibly;
- **Unknown** when not tested.

Passing a direct chat request does not prove agent compatibility. Passing non-streaming does not prove streaming compatibility. A model printing tool syntax does not prove structured tool-call compatibility.

## Reproducibility

Every published report must include enough information to reproduce the result:

- AgentSplice commit and version;
- operating system;
- runtime and version;
- backend and driver stack;
- model repository, filename, hash when practical, and quantization;
- context and offload configuration;
- cache, attention, and speculative-decoding configuration;
- suite and fixture versions;
- relevant client version;
- sanitized configuration bundle.

## CI use

Fast protocol suites may run against a deterministic fake upstream on every pull request. Hardware-dependent suites run in explicitly labeled environments and must not produce universal claims from one machine.

Regression policies may fail CI when:

- a previously passing required case fails;
- a stream becomes invalid;
- gateway-only overhead exceeds an agreed relative threshold under the fake upstream;
- a schema or public contract changes without versioning;
- sensitive content appears in default logs or artifacts.

Absolute model-latency thresholds should not be enforced in shared CI unless the worker hardware is controlled.
