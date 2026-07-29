# Threat model

## Assets

- local source code and prompts;
- model responses;
- tool arguments;
- runtime API credentials;
- replay artifacts;
- model and runtime configuration;
- benchmark data;
- host and GPU metadata;
- integrity of normalization rules.

## Trust boundaries

1. Agent client to AgentSplice.
2. AgentSplice to runtime.
3. AgentSplice to database.
4. Operator to administration API/dashboard.
5. Profile/support-pack files to running gateway.
6. Replay artifact to benchmark worker.

## Threats and mitigations

### Unauthorized inference

Threat: gateway bound to LAN or internet without authentication.

Mitigations: loopback default, startup warning/failure for unsafe bind, bearer/OIDC support, rate limits, audit metadata.

### Credential confusion

Threat: client credential forwarded to an unintended runtime.

Mitigations: runtime-scoped secret references, header allowlist, no default pass-through.

### SSRF

Threat: attacker controls runtime URL.

Mitigations: runtime endpoints are administrative configuration, URI validation, optional private-network policies, no per-request base URL.

### Prompt leakage

Threat: logs, traces, metrics, or error messages contain source code.

Mitigations: body logging off, redaction, bounded labels, safe exception mapping, tests scanning logs.

### Tool-call false positive

Threat: prose is converted to a client-executable tool call.

Mitigations: exact profile syntax, tool allowlist, schema validation, ambiguity rejection, false-positive corpus.

### Malicious upstream stream

Threat: endless frame, giant event, malformed UTF-8/JSON, excessive nesting.

Mitigations: byte/time limits, incremental parsing, depth limits, cancellation, malformed-event policy.

### Replay secret persistence

Threat: stored request contains API key or private token.

Mitigations: header removal, body sanitizer, operator confirmation, retention, encryption roadmap, immutable sanitizer version.

### Profile tampering

Threat: malicious support pack changes normalization or routes traffic.

Mitigations: local administrative access, schema validation, checksums/signatures later, versioned changes, no executable profile code.

### Dashboard XSS

Threat: model output rendered as HTML.

Mitigations: text rendering, CSP, sanitization, no raw HTML mode by default.

### Denial of service

Threat: oversized prompts, too many concurrent streams, expensive replays.

Mitigations: limits, concurrency controls, queues for offline work, cancellation, per-endpoint quotas.

## Security test cases

- authorization header never appears in logs;
- content retention remains off after upgrade;
- arbitrary upstream URL in request is rejected;
- malformed tool syntax does not normalize;
- known secret patterns are removed from replay;
- giant SSE event terminates safely;
- database failure does not leak connection strings;
- dashboard encodes model content.
