# ADR 0027 — Stage 1D: what a local gateway exposes is closed, or said

- Status: Accepted
- Date: 2026-10-18
- Related: [ADR 0003](0003-no-tool-execution.md) (whose wording this amends),
  [ADR 0013](0013-stage-1c-metadata-store.md) (the store whose losses are counted, and decision 5's
  administrative token), [ADR 0015](0015-stage-1d-gateway-serves-the-dashboard.md) (the dashboard),
  [ADR 0026](0026-stage-1d-a-runtime-error-inside-a-success-is-the-runtimes-failure.md) (the batch
  before this one)

## Context

The alpha-readiness audit of 2026-10-17 found that a gateway running on its default loopback binding
was open to two things nobody had decided to allow, and that several documents and settings described
protection or evidence that did not exist.

- **A page on another site could use the gateway.** A page that points its own domain at
  `127.0.0.1` — DNS rebinding — is same-origin with the gateway: it could read `/v1` answers, and
  `/api/v1` when no token is set. Any page could also post a form or plain text to
  `/v1/chat/completions` without the browser asking first. The gateway parsed such a body as JSON, ran
  inference on the operator's runtime, and stored the exchange. THREAT_MODEL.md listed both as
  follow-ups.
- **The token was described as protecting more than it does.** SECURITY.md, SPECIFICATION.md §14.3 and
  THREAT_MODEL.md promised authentication on any binding beyond loopback. The administrative token
  protects `/api/v1` only; `/v1` authenticates no one, as an OpenAI-compatible endpoint for local
  clients does. The binding guard also ignored `Kestrel:Endpoints` and forwarded headers, so a
  deployment could be reachable without the guard noticing.
- **Text a client chose reached places the allowlist does not cover.** The runtime received the
  client's `tracestate` and `baggage` beside the gateway's `traceparent`, and every exported span
  carried the client's `tracestate`. The model-list failure log carried the client's `x-request-id`.
- **Configuration could leak what it names.** The validator and the binding guard repeated a setting
  whose value is the *name* of an environment variable, so an operator who pasted the token itself into
  that setting saw it in the startup error.
- **A string that is not valid UTF-16 was a crash, not malformed input.** `GetString` throws
  `InvalidOperationException` on a lone surrogate escape, which the `JsonException` handlers did not
  catch.
- **Evidence overstated itself.** Generation tokens per second was labelled "reported by runtime";
  AgentSplice derives it. A runtime that streams reasoning in `reasoning_content` or `reasoning` had
  its first semantic event dated at the first answer token. A store that refused every write looked,
  with export off, exactly like a quiet afternoon.
- **What was published was not what was served.** Two Stage 2 operations sat in the OpenAPI document
  with nothing to say this build answers 404 for them, and their `202` bodies were declared under the
  `401` reference beside them. `agentsplice:diagnostics:storeBodies`, `storeHeaders` and
  `headerAllowlist` were shipped and validated, and nothing read them. ADR 0003 said AgentSplice
  "normalizes tool calls"; nothing does.

## Decisions

### 1. Only the loopback names are served unless the operator names others

When ASP.NET Core's `AllowedHosts` is not set, the host filter serves `localhost`, `127.0.0.1` and
`[::1]` only. A rebinding page's requests carry its own domain as the `Host`, so they are refused with a
`400` and no body before routing, before authorization, before AgentSplice sees them. The framework's
failure page, which names the rejected host, is turned off.

The refusal is logged as the framework's `NoAllowedHostMatched` at Information, and the shipped
`appsettings.json` lifts that category above the `Microsoft.AspNetCore` floor so the one place that says
why a `400` has no body is visible by default.

A deployment reached by another name — a Compose service name, `host.docker.internal`, a LAN address,
a reverse proxy — names it in `AllowedHosts`, which replaces the default; `*` restores the framework's.

### 2. A completion body must be declared as JSON

`POST /v1/chat/completions` with a `Content-Type` other than `application/json` (parameters allowed),
or with none, is answered `415` with `agentsplice_invalid_request`, before the body is read. It is not
forwarded and not recorded.

`text/plain`, `application/x-www-form-urlencoded` and `multipart/form-data` are what a page can send
cross-site without a preflight. A JSON content type needs one, and the gateway never answers a
preflight with CORS headers, so the browser never sends the request. Not recording the refusal is
deliberate: a page that cannot run inference must not be able to fill the store instead. Every SDK and
every client this project documents sends `application/json`; `curl -d` without `-H` does not.

### 3. The binding guard reads every way a gateway can be reached, and startup says what that means

`Kestrel:Endpoints` is read as well as `urls`: a configured endpoint suppresses the loopback default,
and one that is not loopback, has no URL, or is a socket or pipe counts as reachable.
`FORWARDEDHEADERS_ENABLED` counts as reachable too, because a proxy in front makes every caller look
local. A reachable gateway still refuses to start without an administrative token.

A gateway that listens beyond loopback logs event `1205` (`InferenceReachableBeyondLoopback`) at
Warning when it starts: `/v1` authenticates no one, and the token protects `/api/v1` only. The documents
now say the same.

### 4. Nothing a client chose leaves through trace context, and logs keep none of it

The exchange span no longer inherits the caller's `tracestate`; the trace id still continues the
caller's trace. The runtime receives the gateway's own `traceparent` and nothing else: a propagator in
the LM Studio provider drops `tracestate` and `baggage`. The model-list failure log no longer carries
the request id.

### 5. A setting that names a variable is never repeated

The validator and the binding guard name the setting and say what is wrong without repeating its
value, so a token pasted into the setting by mistake stays out of logs. The administrative token's
variable name is validated like a runtime's. The runtime credential warning no longer names the
variable.

### 6. Text that is not valid UTF-16 is malformed

A lone surrogate escape makes a request `400 agentsplice_invalid_request`, a stream event malformed, a
buffered answer uninterpretable, and a catalogue entry skipped, as any other undecodable value is.

### 7. Evidence says what it is

- The dashboard labels generation tokens per second as derived: tokens the runtime reported, over a
  window AgentSplice measured. A derived value carries the provenance of its weakest input, which
  OBSERVABILITY.md now states.
- A delta whose `reasoning_content` or `reasoning` carries text is semantic output, as `content` is. Only
  whether the field carries text is read.
- The timing waterfall says where each phase is measured from.
- Exported token metrics are documented as runtime-reported.

### 8. Lost evidence is counted where an operator looks

`GET /api/v1/system` reports `persistenceFailures`: exchanges whose evidence was dropped because the
metadata queue was full, and because the store rejected the batch carrying them, since the gateway
started. The overview shows a notice when either is above zero. Events `1301` and `1302` are documented
beside them. The counts are not persisted: they describe this process, and a count that survived a
restart would describe a store that may since have been fixed.

### 9. What is published is what is served

- The Stage 2 operations carry `x-agentsplice-planned: true` and say they are not served; their `202`
  bodies are declared where a reader looks. A contract test holds the marker to the Stage 2 section of
  API.md, and an integration test holds "not served" to a `404`.
- A contract test reads every path the dashboard's client requests and requires the OpenAPI document to
  publish it.
- `storeBodies` must be false and `storeHeaders` must be `None`, refused as `capture:contentEnabled`
  is; `headerAllowlist` is removed, and the section is gone from `appsettings.json`.
- ADR 0003 keeps its decision — tool calls are returned to the client, which approves and executes
  them — with a note that no stage so far normalizes one.

### 10. CI runs on the image it was proved on

The workflows pin `ubuntu-24.04` instead of `ubuntu-latest`, which GitHub moves to 26.04 from
2026-10-19. A move of the build image is a change to review, not one to discover in a release run.

## Consequences

- A client that sends a completion without `Content-Type: application/json` gets `415`. Every
  documented example already sends it.
- A deployment reached by a name other than the loopback names must set `AllowedHosts`, or every
  request is a `400` with no body. INSTALLATION.md and CLIENTS.md say so, and the log says which host was
  refused.
- A configuration that set `storeHeaders` to `Allowlist` — the value the old `appsettings.json` shipped —
  fails startup. No release carried it.
- A rebinding page and a cross-site form are closed; a local process is not, by design. `/v1` serves
  whatever can reach it, and binding beyond loopback is now said aloud at startup.

## Rejected alternatives

- **Authenticating `/v1` with the administrative token.** It would break every client configured as
  the documents say, for a threat the two refusals already close on loopback; a network deployment that
  wants it belongs behind its own proxy.
- **Answering CORS preflights with a deny.** A browser treats a missing CORS answer as a deny already.
- **Recording the `415` as an exchange.** It would give a cross-site page a way to write to the store.
- **Keeping the diagnostics flags as accepted no-ops.** An accepted flag is a claim, the reason
  `capture:contentEnabled` has been refused since Stage 1C.
