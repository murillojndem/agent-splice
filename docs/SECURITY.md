# Security requirements

## Defaults

- Listen on loopback unless explicitly configured otherwise. The default is applied as a fallback when no binding is configured, so that `ASPNETCORE_URLS` and container port settings still take effect. In a container the process binds the container interface and the operator maps the published port to loopback on the host; see ADR 0007.
- Require authentication when listening on a non-loopback address.
- Do not store request or response bodies by default.
- Do not log authorization headers, prompts, source code, tool arguments, or model output.
- Do not execute tools in the core gateway or replay subsystem.
- Do not permit clients to supply arbitrary upstream URLs.
- Apply request-body, header, event, stream-duration, and concurrency limits. Stage 1A bounds the
  request body (4 MiB), the upstream completion body (64 MiB), and the upstream model catalogue
  (4 MiB); reading stops at the bound plus one byte. A concurrency limit is owed by Stage 1B.
- Never forward the client's `Authorization` header upstream, and never relay an upstream
  `WWW-Authenticate` or `Set-Cookie` to the client. Both directions use an allowlist, because a
  denylist admits every header invented after it was written.
- Redact credential-bearing headers from HTTP client logging. `IHttpClientFactory` logs request
  headers at `Trace`, so without redaction the runtime's bearer token reaches any enabled sink.
- Treat dashboard and administrative APIs as sensitive even in local deployments.

## Authentication

Stage 1 may support a static bearer token for local-network use. Store only a secret reference or appropriate hash. Future deployments may add OIDC, but OIDC is not required for the first alpha.

## Upstream credentials

Each runtime endpoint references its own secret source. Credentials are attached only to that runtime. Never forward a client authorization header upstream unless an explicit trusted pass-through policy exists.

## Content retention

Prompts, responses, source code, tool schemas, tool arguments, file paths, and replay data are sensitive. Metadata must remain useful without body content.

Content storage requires:

- explicit opt-in;
- sanitizer selection and version;
- authorization;
- visible UI state;
- independent retention;
- deletion support;
- export review;
- encryption decision appropriate to deployment.

Sanitization occurs before persistence, not after.

## Trace safety

- Metric labels must be bounded.
- Span attributes must not contain arbitrary content.
- Safe structural summaries must not reconstruct sensitive payloads accidentally.
- Errors must not expose upstream credentials, connection strings, or raw response bodies.
- Unknown values remain unknown rather than being derived from sensitive content without policy.

## Replay safety

Replay artifacts must remove credentials and apply deterministic sanitization. Replay targets require allowlists, concurrency controls, timeouts, and audit events. Replay treats tool calls as data and never executes them.

Exact replay means exact within the sanitized artifact and declared environment constraints; it does not justify restoring secrets.

## Evaluation execution safety

Agentic evaluations are later-stage, separately permissioned workers. They must use disposable environments, explicit command/tool allowlists, filesystem scope, network policy, resource limits, timeouts, and cleanup.

The evaluation worker must not share the core gateway's authority by default.

## Compatibility adapter safety

Adapters can change semantics and therefore require:

- disabled-by-default or explicit profile selection;
- stable ID/version;
- precise activation constraints;
- fixtures and adversarial tests;
- ambiguity rejection;
- transformation manifests;
- failure policy;
- upstream status and retirement criteria.

Text-to-tool recovery is particularly sensitive because it can turn inert text into a client-executable instruction. It is deferred beyond Stage 1.

## Dependency and release security

- automated dependency updates with review;
- pinned container strategy;
- SBOM generation;
- vulnerability scanning;
- signed release artifacts when practical;
- no unreviewed dynamic plugin loading;
- provenance for community adapters and profiles.

## Disclosure

See root `SECURITY.md` for the public vulnerability-reporting policy.
