# Threat model

## Assets

- local source code and prompts;
- model responses and tool arguments;
- runtime/API credentials;
- trace metadata and optional content;
- replay artifacts;
- conformance and evaluation fixtures;
- model/runtime configuration;
- benchmark and regression data;
- host/GPU metadata;
- adapter/profile integrity;
- evaluation-worker authority.

## Trust boundaries

1. Agent client to AgentSplice ingress.
2. AgentSplice to runtime/provider.
3. AgentSplice to database/storage.
4. Operator to administration API/dashboard.
5. Adapter/profile/support-pack files to running AgentSplice.
6. Exchange data to sanitizer and replay artifact.
7. Replay worker to target runtime.
8. Evaluation orchestrator to disposable execution environment.
9. Exported issue bundle to external recipients.
10. AgentSplice to an OpenTelemetry collector.

## Threats and mitigations

### Unauthorized inference or diagnostics access

Threat: service bound to LAN/internet without adequate authentication.

Mitigations: loopback default, startup refusal/warning for unsafe bind, authentication, rate limits, audit metadata, separate administration authorization.

### Credential confusion

Threat: client credential forwarded to an unintended runtime or persisted in evidence.

Mitigations: runtime-scoped secret references, header allowlists, no default pass-through, sanitization before persistence/export.

### SSRF and arbitrary targets

Threat: attacker controls runtime or replay target URL.

Mitigations: administrative configuration only, URI validation, target allowlists, network policy, no per-request arbitrary base URL.

### Prompt/source leakage

Threat: logs, traces, metrics, dashboard, replay, or issue bundles expose private content.

Mitigations: metadata-only default, bounded attributes, content authorization, sanitization, retention, export review, automated leakage tests.

### Telemetry sent somewhere unintended

Threat: spans and metrics — a description of every exchange, though no content — reach a collector the operator did not choose, or leak content through an attribute, or an unreachable collector degrades the request path.

Mitigations: export off by default with no default destination; the standard `OTEL_EXPORTER_OTLP_ENDPOINT` variable ignored so an ambient setting cannot enable or redirect export; endpoint validation refusing credentials, queries, and misplaced signal paths; a startup log line naming the destination; export reported by `/api/v1/system`; bounded attributes only, with an integration test searching every exported byte for prompt, output, tool-argument, model, request-identifier, and credential sentinels; export on background threads with a bounded queue; no log export (ADR 0014).

### Malicious upstream stream

Threat: endless frames, giant events, malformed UTF-8/JSON, excessive nesting, duplicate terminals, or slowloris behavior.

Mitigations: byte/time/depth limits, incremental parsing, idle timeout, cancellation, explicit malformed-stream termination.

### Tool-call false positive

Threat: optional adapter converts prose into a client-executable call.

Mitigations: deferred adapter stage, exact profile syntax, tool allowlist, argument/schema validation, ambiguity rejection, adversarial corpus, disabled-by-default operation.

### Replay secret persistence

Threat: artifact retains API keys, tokens, private paths, or customer data.

Mitigations: sanitizer before storage, header removal, structured redaction, secret detection, deterministic placeholders, integrity/sanitizer version, retention and deletion.

### Replay amplification or unsafe execution

Threat: replay floods a runtime or executes client tools.

Mitigations: concurrency/rate limits, target allowlists, cancellation/timeouts, no tool execution, audit events.

### Evaluation-worker escape

Threat: agent commands affect host files/network or persist outside the scenario.

Mitigations: disposable containers/VMs, least privilege, read-only mounts where possible, resource limits, network deny-by-default, command allowlists, cleanup verification.

### Adapter/profile tampering

Threat: malicious support pack reroutes traffic, weakens sanitization, or changes semantics.

Mitigations: schema validation, local administrative access, checksums/signatures later, versioned changes, no executable profile code, security review, adapter provenance.

### Dashboard XSS

Threat: model output or fixture content renders as active HTML/script.

Mitigations: text rendering, output encoding, CSP, no raw HTML mode by default, sanitized previews. The gateway serves the dashboard on the administrative API's origin with a policy on every response that admits only same-origin script, style, and connections and no inline script or `eval`, so a rendering mistake cannot run injected script against the token the page holds in memory (FR-DASH-008, ADR 0015).

### Dashboard framing

Threat: another page loads the dashboard in a frame and overlays it to steer an operator's clicks or read what it shows.

Mitigations: `frame-ancestors 'none'` and `X-Frame-Options: DENY` on every dashboard response. The cost is that an editor's embedded browser that renders in an iframe cannot show the dashboard (ADR 0015).

### Cross-origin requests to a loopback gateway

Threat: a web page the operator visits sends requests to a gateway on `127.0.0.1`. With no administrative token configured, loopback callers are served, so DNS rebinding — a hostile hostname re-resolved to `127.0.0.1` — makes the page same-origin with the gateway and lets it read `/api/v1`. Separately, the ingress does not require a JSON content type, so any page can send a cross-site "simple" `POST` to `/v1/chat/completions` that runs inference and is recorded as an exchange, though it cannot read the answer.

Status: residual, and unchanged by serving the dashboard — an attacker needs only the API, which was reachable the same way before. Mitigations today: configuring an administrative token closes the rebinding read of `/api/v1`, and nothing reads a response cross-origin without CORS, which the gateway never sends. Follow-ups: a Host allowlist for the loopback default, and refusing completion requests that are not `application/json`.

### Dashboard asset tampering

Threat: something able to write to the gateway's files replaces the dashboard's script, which then runs with every operator's token.

Status: residual. In the image the build sits under `/app`, which the process user owns, as it owns the gateway's own assemblies; making the dashboard alone root-owned would protect nothing, because the owner of a directory can replace its contents. Follow-up: a read-only `/app`.

### Container health probe

Threat: the image's health check becomes a way to reach something other than the gateway — a request to another host, a proxy, or a redirect target — or a way to report health that is not the gateway's.

Mitigations: the probe takes its one target from the image configuration, never from a request, and refuses anything but an `http` URL whose host is a loopback IP literal — no name resolution, no user information. It uses no proxy, follows no redirect, reads the status line and headers only, and gives up after three seconds. It references only the base class library, nothing references it, and it runs as the image's non-root user under `/app`, so replacing it requires the same write access that replacing the gateway does. Its one line of output names a status or a failure class and never echoes a target it refused (ADR 0016).

### Misleading evidence

Threat: estimates are presented as measurements, unsupported runs are omitted, or a report overgeneralizes from one environment.

Mitigations: provenance, immutable raw results, environment snapshots, status taxonomy, unsupported-result retention, report validation.

### Denial of service

Threat: oversized prompts, streams, trace volumes, replays, or evaluation jobs exhaust resources.

Mitigations: size/concurrency/retention limits, bounded queues, separate workers, backpressure, cancellation, per-endpoint quotas.

## Security test cases

- authorization and runtime secrets never appear in logs/traces/artifacts;
- nothing is exported unless OTLP export is explicitly enabled, and exported spans and metrics carry no content;
- content retention remains off after upgrade;
- arbitrary upstream/replay URL is rejected;
- giant or malformed SSE terminates safely;
- known secret patterns are removed before replay persistence;
- replay never invokes tools;
- evaluation cannot modify host fixture source outside mounted workspace;
- database failure does not leak connection strings;
- dashboard encodes all content;
- every dashboard response carries a policy forbidding inline script, `eval`, cross-origin connections, and framing;
- nothing outside the dashboard build directory, and no hidden file in it, is served;
- serving the dashboard changes no answer outside `/dashboard`;
- the health probe refuses any target off loopback `http` without sending a request, and exits only 0 or 1;
- adapter cannot activate outside declared constraints;
- exported reports include provenance and omit secret values.
