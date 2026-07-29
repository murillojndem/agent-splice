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

Mitigations: text rendering, output encoding, CSP, no raw HTML mode by default, sanitized previews.

### Misleading evidence

Threat: estimates are presented as measurements, unsupported runs are omitted, or a report overgeneralizes from one environment.

Mitigations: provenance, immutable raw results, environment snapshots, status taxonomy, unsupported-result retention, report validation.

### Denial of service

Threat: oversized prompts, streams, trace volumes, replays, or evaluation jobs exhaust resources.

Mitigations: size/concurrency/retention limits, bounded queues, separate workers, backpressure, cancellation, per-endpoint quotas.

## Security test cases

- authorization and runtime secrets never appear in logs/traces/artifacts;
- content retention remains off after upgrade;
- arbitrary upstream/replay URL is rejected;
- giant or malformed SSE terminates safely;
- known secret patterns are removed before replay persistence;
- replay never invokes tools;
- evaluation cannot modify host fixture source outside mounted workspace;
- database failure does not leak connection strings;
- dashboard encodes all content;
- adapter cannot activate outside declared constraints;
- exported reports include provenance and omit secret values.
