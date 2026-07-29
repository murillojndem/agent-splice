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

## Compatibility policy

AgentSplice should preserve unknown request fields when safe and practical. Any dropped or changed field must appear in a routing or adapter event.

Modes:

- `transparent`: forward supported and unknown provider-neutral fields where possible;
- `strict`: reject unsupported fields;
- `adapted`: apply only explicitly selected, versioned adapters and produce a manifest.

A conformance report, not endpoint availability alone, determines a support claim.
