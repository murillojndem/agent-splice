# API specification notes

The normative machine-readable draft is in `openapi/agentsplice-openapi.yaml`.

AgentSplice exposes two API surfaces:

1. compatibility endpoints used by agent clients;
2. AgentSplice administrative endpoints used by the dashboard, replay, conformance, and diagnostics.

## OpenAI-compatible ingress

### `GET /v1/models`

Returns configured and discovered client-visible model IDs. Provider-specific administrative detail belongs under `/api/v1`.

The catalogue combines, per enabled runtime in configuration order:

1. every enabled alias targeting that runtime, ordered by priority then declaration order;
2. every model discovered from that runtime, when its discovery is enabled.

Aliases are configuration, so they are offered whether or not discovery ran. A runtime with discovery disabled remains fully usable through its aliases.

Duplicate client-visible identifiers are collapsed to one entry. The winner is an alias over a discovered model, then the earlier runtime in configuration order, then alias priority. FR-MOD-004 disambiguates duplicates *internally* by runtime endpoint ID: the listed `id` stays the bare model identifier, because a composite `runtime/model` identifier would be a value AgentSplice invented, and a client copying it back into `model` would send something no runtime recognises. The full multi-runtime picture belongs to `/api/v1/models`.

Status:

| Situation | Status |
|---|---|
| At least one alias or discovered model is known | `200` |
| Some runtimes failed, others answered | `200` with what is known |
| Every consulted runtime failed and nothing is known | `502` `agentsplice_runtime_unavailable` |
| Nothing configured, or every runtime disabled | `200` with an empty `data` array |

The last row is deliberate: an empty catalogue caused by configuration is an operator fact, and reporting it as an upstream outage would send a user looking in the wrong place.

`created` is a Unix timestamp, so zero means 1970-01-01 rather than "unknown". A discovered model passes through whatever the runtime reported. An alias inherits the value of the model it targets when that model was discovered. When there is no creation evidence at all the envelope emits `0` purely because the OpenAI schema marks the field required and integral and mainstream SDKs deserialize it into a non-nullable integer. That substitution exists only in this envelope; the value is never stored, compared, or re-read as a date, and `/api/v1/models` reports the honest absence alongside `capabilityProvenance`.

### `POST /v1/chat/completions`

Supports streaming and non-streaming operation. Stage 1 prioritizes transparent forwarding, valid SSE, cancellation, stable errors, and trace capture. Stage 1 does not reinterpret text as tool calls.

**Streaming is not yet implemented.** A request with `stream: true` is refused with `400` and `param: "stream"`. Buffering an event stream into a single JSON body would be an invisible semantic transformation, and answering `200` would make an unimplemented capability look implemented.

Forwarding is byte-preserving. When routing does not rename the model, the runtime receives the client's original bytes unchanged. When an alias renames it, only the bytes of the top-level `model` value are replaced; property order, escape forms, number formatting, and insignificant whitespace are all preserved, because the body is spliced rather than reparsed and re-emitted.

Only `model` and `messages` are validated. Every other field, known or unknown, is forwarded verbatim, and unknown top-level names are recorded so transparent forwarding is verifiable. A field that determines handling — `model`, `messages`, or `stream` — supplied more than once is refused, because "last wins" can differ between AgentSplice and the runtime.

The response is relayed unchanged: its status, its body, and the headers on the relay allowlist. The response `model` field is **not** rewritten back to the client's alias, because that is not required for routing (P-002). The body is parsed only to gather evidence, and a body that cannot be parsed costs a structural summary and nothing else.

Headers crossing the gateway are allowlisted in both directions:

| Direction | Forwarded |
|---|---|
| Client to runtime | `Content-Type`, `Accept`, `x-request-id` (the AgentSplice correlation token), and the runtime's own `Authorization` |
| Runtime to client | `Content-Type`, `Retry-After`, `x-ratelimit-*` |

The client's `Authorization` header is never forwarded upstream, and no hop-by-hop header is copied in either direction. A relayed `429` keeps its `Retry-After`, without which the status conveys nothing actionable.

## Gateway headers

Accepted request headers:

```text
x-request-id
x-agentsplice-diagnostics
x-agentsplice-profile         # Later adapter stages
x-agentsplice-capture-content # Explicitly authorized environments only
```

Response headers:

```text
x-agentsplice-request-id
x-agentsplice-runtime
x-agentsplice-exchange-id
x-agentsplice-trace-id
x-agentsplice-adapters        # Present only when adapters ran
```

Headers must not contain prompt content, tool arguments, credentials, arbitrary model output, or high-cardinality trace details.

## Stage 1 administrative endpoints

```http
GET /api/v1/system
GET /api/v1/health/runtimes
GET /api/v1/runtimes
GET /api/v1/models
GET /api/v1/exchanges
GET /api/v1/exchanges/{id}
GET /api/v1/exchanges/{id}/timeline
GET /api/v1/exchanges/{id}/observations
```

The exchange list defaults to metadata only. Content endpoints must not exist until content retention, sanitization, authorization, and retention policies are implemented.

## Stage 2 replay and conformance endpoints

```http
POST /api/v1/replay-artifacts
GET  /api/v1/replay-artifacts/{id}
POST /api/v1/replays
GET  /api/v1/replays/{id}
GET  /api/v1/replays/{id}/comparison

GET  /api/v1/conformance/suites
POST /api/v1/conformance/runs
GET  /api/v1/conformance/runs/{id}
GET  /api/v1/conformance/runs/{id}/cases
GET  /api/v1/compatibility-matrix
```

## Stage 3 evaluation endpoints

```http
GET  /api/v1/evaluations/scenarios
POST /api/v1/evaluations/runs
GET  /api/v1/evaluations/runs/{id}
GET  /api/v1/evaluations/runs/{id}/artifacts
GET  /api/v1/regressions
```

## Later adapter/profile endpoints

```http
GET  /api/v1/adapters
GET  /api/v1/adapters/{id}
GET  /api/v1/profiles
GET  /api/v1/profiles/{id}
POST /api/v1/adapter-validations
```

Mutation of profiles through the UI is deferred until configuration provenance, validation, authorization, and safe rollback are defined.

## Stable error codes

Core:

- `agentsplice_invalid_request`
- `agentsplice_model_not_found`
- `agentsplice_runtime_not_found`
- `agentsplice_runtime_unavailable`
- `agentsplice_runtime_authentication_failed`
- `agentsplice_upstream_timeout`
- `agentsplice_invalid_upstream_response`
- `agentsplice_invalid_upstream_stream`
- `agentsplice_request_cancelled`
- `agentsplice_persistence_unavailable`
- `agentsplice_internal_error`

Later stages:

- `agentsplice_replay_artifact_invalid`
- `agentsplice_replay_target_forbidden`
- `agentsplice_conformance_suite_not_found`
- `agentsplice_evaluation_environment_failed`
- `agentsplice_adapter_failed`
- `agentsplice_translation_loss`

## Stable error types

Every error envelope carries a `type` alongside its `code`. The `code` is the stable machine-readable identity; the `type` is the coarse category a client switches on.

Client-validation failures reuse OpenAI's own `invalid_request_error`, including model-not-found, so an SDK that branches on `type` keeps working. The remaining categories exist because a plain model provider has no vocabulary for them, and flattening them into one would discard the only distinction that matters: which side of the gateway failed.

- `invalid_request_error`
- `configuration_error`
- `upstream_unavailable_error`
- `upstream_authentication_error`
- `upstream_timeout_error`
- `upstream_protocol_error`
- `cancellation_error`
- `internal_error`

## Error status mapping

| Trigger | Code | Status | Type |
|---|---|---|---|
| Body is not valid JSON, not an object, or fails validation | `agentsplice_invalid_request` | 400 | `invalid_request_error` |
| Body exceeds the configured maximum size | `agentsplice_invalid_request` | 413 | `invalid_request_error` |
| Model resolves to nothing | `agentsplice_model_not_found` | 404 | `invalid_request_error` |
| Resolved runtime has no provider module | `agentsplice_runtime_not_found` | 503 | `configuration_error` |
| Runtime unreachable, or no runtime could be consulted | `agentsplice_runtime_unavailable` | 502 | `upstream_unavailable_error` |
| Runtime rejected the gateway's credentials | `agentsplice_runtime_authentication_failed` | 502 | `upstream_authentication_error` |
| A configured timeout phase elapsed | `agentsplice_upstream_timeout` | 504 | `upstream_timeout_error` |
| Upstream **2xx** body unreadable, truncated, or oversized | `agentsplice_invalid_upstream_response` | 502 | `upstream_protocol_error` |
| Client disconnected | `agentsplice_request_cancelled` | *(nothing written)* | `cancellation_error` |
| Unhandled gateway fault | `agentsplice_internal_error` | 500 | `internal_error` |
| **Any other upstream non-2xx** | *(none — the runtime's own body)* | **upstream status, verbatim** | *(the runtime's own)* |

There is no AgentSplice error type for a relayed upstream status: the runtime's own envelope is what the client receives, so the gateway writes nothing for a type to describe.

An upstream `401` or `403` is never echoed to the client. The credential is the gateway's, not the client's, so returning `401` would tell a client to fix a key it does not own, and the upstream body is discarded because it can hint at the key's shape.

Every other non-2xx is relayed unchanged, whether or not its body is JSON. Parsing gathers evidence and never gates forwarding: a runtime answering `429 text/plain` is still answering, and substituting a gateway error would discard the most actionable diagnostic a user has. The exchange completes with no failure class in that case — the transport cycle finished and AgentSplice did not fail — and the runtime's status is recorded separately, which is what success and error are classified from.

Error messages are compile-time constants. None is derived from an upstream message, a response body, an exception, or a URL, so no error can disclose a credential, an internal hostname, or model output.

## Compatibility policy

AgentSplice should preserve unknown request fields when safe and practical. Any dropped or changed field must appear in a routing or adapter event.

The mode is set by `agentsplice:compatibility:unsupportedFields` and is explicit rather than implied, because both behaviours are defensible and what matters is being able to tell which one a deployment has (FR-CHAT-005).

- `transparent` *(default)*: forward supported and unknown provider-neutral fields where possible. The runtime is the authority on its own protocol, so refusing a field it would have accepted would make AgentSplice the source of a failure that does not exist downstream.
- `strict`: reject a request carrying any top-level field AgentSplice does not model, naming that field as `param`. For deployments that would rather fail loudly than discover later that a field was passed through untouched. It constrains only top-level names, because nested shapes are ones the gateway never claimed to understand.
- `adapted`: apply only explicitly selected, versioned adapters and produce a manifest. **Not implemented** — adapters are a Stage 4 capability, and the value is not accepted by configuration, because a mode that cannot be applied would be a policy in name only.

A conformance report, not endpoint availability alone, determines a support claim.
