# Security requirements

## Defaults

- Listen on loopback unless explicitly configured otherwise. The default is applied as a fallback when no binding is configured, so that `ASPNETCORE_URLS` and container port settings still take effect. In a container the process binds the container interface and the operator maps the published port to loopback on the host; see ADR 0007.
- Require authentication when listening on a non-loopback address.
- Do not store request or response bodies by default.
- Do not log authorization headers, prompts, source code, tool arguments, or model output.
- Do not execute tools in the core gateway or replay subsystem.
- Do not permit clients to supply arbitrary upstream URLs.
- Apply request-body, header, event, stream-duration, and concurrency limits. The request body
  (4 MiB), the upstream completion body (64 MiB), and the upstream model catalogue (4 MiB) are all
  bounded, and reading stops at the bound plus one byte. A single streamed event is bounded by
  `limits:maxStreamEventBytes` (1 MiB), stream duration by the runtime's `timeouts:total` and
  `timeouts:idleStream`, and concurrency by `limits:maxConcurrentCompletions` (64), which refuses
  with `429` rather than queueing — a queue converts an overload into unbounded latency, which an
  agent loop cannot act on.

  A streamed exchange retains a 16 KiB read buffer plus the event it is currently assembling, and
  nothing else: bytes reach the client and are released. The streaming path's memory ceiling is
  therefore `(16 KiB + maxStreamEventBytes) × maxConcurrentCompletions`, or roughly 66 MiB at the
  defaults, and does not grow with the length of a response.
- Clear pooled buffers that held prompt or response content before returning them. A pooled array
  outlives the exchange that filled it, and the classic way content escapes is a later renter that
  trusts the array's length instead of its read count. The buffers are rented once per exchange
  rather than once per read, so clearing costs a single memset against a stream that ran for seconds.
- Never forward the client's `Authorization` header upstream, and never relay an upstream
  `WWW-Authenticate` or `Set-Cookie` to the client. Both directions use an allowlist, because a
  denylist admits every header invented after it was written.
- Redact credential-bearing headers from HTTP client logging. `IHttpClientFactory` logs request
  headers at `Trace`, so without redaction the runtime's bearer token reaches any enabled sink.
- Treat dashboard and administrative APIs as sensitive even in local deployments.

## Dashboard

The gateway serves the dashboard build at `/dashboard/` on its own origin (ADR 0015):

- **Same origin as `/api/v1`, no CORS.** The dashboard calls the administrative API by relative path
  and sends the token as a bearer header held in memory, never a cookie, so no other site can ride on
  an operator's session.
- **The page is public; the evidence is not.** The shell and its assets are served without the
  administrative token: they are the same code for everyone and hold no evidence. Every `/api/v1` call
  the page makes is subject to the token rule below, unchanged.
- **A strict content security policy on every dashboard response.** Only same-origin script, style,
  and connections; no inline script, no `eval`, no framing (`frame-ancestors 'none'` and
  `X-Frame-Options: DENY`), plus `X-Content-Type-Options: nosniff` and `Referrer-Policy: no-referrer`.
  The page renders strings clients and runtimes chose while holding the token, so a rendering mistake
  must not become script (FR-DASH-008).
- **`agentsplice:dashboard:rootPath` is trusted to the operator.** Every file beneath it with a known
  content type is served, dot-prefixed and hidden files excepted, so it must name the build and nothing
  else. A directory without `index.html` and an `assets` directory is not treated as a build.
- **It can be turned off.** `agentsplice:dashboard:enabled: false` serves nothing at `/dashboard/` and
  leaves `/` a 404, for a deployment that ships the build but wants no browser-facing surface.

## Local installation

The Windows package (ADR 0017, `docs/INSTALLATION.md`) runs as the user who starts it, from a folder
that user extracted:

- **The folder is trusted like `/app` in the image.** Whoever can write it can replace the executable,
  its settings, and the dashboard's script. Install it under the user's own profile
  (`%LOCALAPPDATA%\Programs`), not in a folder every user can write; a folder created directly under
  `C:\` inherits write access for all authenticated users.
- **The store holds evidence.** `agentsplice.db` beside the executable, or wherever
  `agentsplice:persistence:connectionString` points, holds the structural metadata and identifiers
  this document lists as retained, unencrypted. It is protected by the folder's permissions alone, and
  so are copies of it.
- **The default is loopback without a token,** which serves `/api/v1` to every local caller. Setting a
  token is recommended even so; the installation guide shows how, and the threat model records the
  browser-based read it closes.
- **The bundled runtime is not serviced.** A self-contained package carries the .NET runtime it was
  built with, and Windows Update does not patch it. .NET security fixes reach a package only through a
  new AgentSplice release; `README.txt` in each package names the runtime version.
- **The package is not code signed.** Its integrity rests on the SHA-256 digest published beside it
  and verified as the installation guide describes.

## Authentication

Stage 1 supports a static bearer token for the administrative API. Only a secret reference is stored:
`agentsplice:administration:apiKeyEnvironmentVariable` holds the *name* of an environment variable.
Future deployments may add OIDC, but OIDC is not required for the first alpha.

The rule is chosen by whether a token is configured, and there is deliberately no middle setting:

- **A token is configured.** Every `/api/v1` request carries it, including one arriving from loopback.
- **No token is configured.** Only loopback is served, and startup refuses a binding a network can
  reach, so this case cannot be combined with exposure by forgetting.

A loopback remote address is not evidence of a local caller. A reverse proxy on the same host connects
to Kestrel from `127.0.0.1`, so a relayed external request is indistinguishable from a local one at
the socket. Trusting `X-Forwarded-For` without Forwarded Headers Middleware configured against known
proxies would make it worse — that header is caller-supplied — so the token is required from everyone
once one exists. A trusted-proxy deployment needs `KnownProxies`/`KnownNetworks` configuration that
this stage does not implement.

The container configuration therefore requires a token: a process inside a container binds every
interface it can see and cannot tell that the host published the port on loopback only.

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
- Safe structural summaries must not reconstruct sensitive payloads accidentally. Bounding a value's
  length and count is not enough on its own: a client chooses the value of `role` and the name of any
  JSON property it sends, and a runtime chooses the value of `finish_reason`, so truncating those
  bounds how much caller-chosen text is retained and nothing else. Every string a summary holds is
  therefore either drawn from a closed protocol vocabulary and bucketed when it does not match, or
  hashed. See `SafeVocabulary`. Matching is exact and ordinal: a value is the token the protocol
  defines or it is bucketed, because recording `" User "` as `user` would store a corrected version of
  the protocol and present it as what was observed. The buckets are outputs and are never accepted as
  inputs, so a client cannot send `"(unspecified)"` and be recorded as having sent no role at all.
- Three identifiers are **operational metadata retained verbatim**: `ClientModelId`,
  `PublicRequestId`, and `UpstreamRequestId`. Each is chosen by an untrusted party — the client names
  the model and the correlation token, the runtime names its own request ID — and each is bounded
  printable text rather than safe text. They are kept whole because diagnosis needs them: an operator
  correlating a client's complaint to a stored exchange has nothing else to match on. Treat them as
  potentially sensitive. They must not reach a log, a metric dimension, or an export, and the
  administrative APIs that serve them require the same authorization as any other stored evidence.
- Never log the client's correlation token. `x-request-id` is accepted as up to 128 characters of
  printable ASCII, which prevents header injection and nothing else; a client is free to put a name,
  a ticket subject, or a path in it. `ExchangeId` is AgentSplice's own identifier, is returned as
  `x-agentsplice-exchange-id`, and is what logs carry. The correlation token is also redacted from
  `IHttpClientFactory` header logging, because AgentSplice forwards it upstream.
- Telemetry export is a data egress path and is treated as one (ADR 0014). It is off by default, its
  destination is set only by `agentsplice:telemetry:otlp:endpoint` — never by
  `OTEL_EXPORTER_OTLP_ENDPOINT` — and an endpoint embedding credentials is refused at startup,
  because the endpoint is logged when export begins. A collector credential belongs in
  `OTEL_EXPORTER_OTLP_HEADERS`, which the SDK reads and AgentSplice neither stores nor logs. Exported
  spans and metrics carry the bounded attributes of `docs/OBSERVABILITY.md` and nothing a client or
  model chose; logs are not exported.
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
