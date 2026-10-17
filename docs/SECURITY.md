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

The Windows and Linux packages (ADR 0017, ADR 0021, `docs/INSTALLATION.md`) run as the user who
starts them, from a folder that user extracted:

- **The folder is trusted like `/app` in the image.** Whoever can write it can replace the executable,
  its settings, and the dashboard's script. Install it under the user's own profile
  (`%LOCALAPPDATA%\Programs` on Windows, a folder set to mode 700 under the home directory on Linux),
  not in a folder every user can write; a folder created directly under `C:\` inherits write access for
  all authenticated users. The Linux archive records folders and the executable as `rwxr-xr-x` and every
  other file as `rw-r--r--`.
- **The store holds evidence.** `agentsplice.db` beside the executable, or wherever
  `agentsplice:persistence:connectionString` points, holds the structural metadata and identifiers
  this document lists as retained, unencrypted. It is protected by the folder's permissions alone, and
  so are copies of it. On Linux, SQLite creates the file readable by whomever the umask allows — usually
  every account on the machine — so the guide sets the folders it suggests to mode 700. The folder
  protects the file only; see the next point for what the gateway serves.
- **The default is loopback without a token,** which serves `/api/v1` to every local caller — every
  account on the machine, not only the one that started the gateway, while it runs. Setting a token is
  recommended even so, and on a machine with other accounts it is what keeps the evidence to its owner;
  the installation guide shows how, and the threat model records the browser-based read it also closes.
- **The bundled runtime is not serviced.** A self-contained package carries the .NET runtime it was
  built with, and neither Windows Update nor a Linux distribution's updates patch it. .NET security
  fixes reach a package only through a new AgentSplice release; `README.txt` in each package names the
  runtime version. Microsoft supports .NET 10 until November 14, 2028; after that date no .NET 10 build
  receives runtime fixes.
- **The packages are not signed.** Their integrity rests on the SHA-256 digests published beside them
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

## Client configuration

- `/v1` authenticates nobody, so a client's API key field holds a placeholder. Never put the
  administrative token or a runtime's key into a client: the gateway discards it, and client settings —
  a project's `opencode.json` in particular — are commonly committed to repositories.
- The sample client configuration carries no credential and points at the IPv4 loopback literal the
  gateway binds, and a contract test holds both (ADR 0019).
- Set `OPENAI_BASE_URL` for one command or session. Set for the account, it sends every OpenAI SDK
  program on the machine to the gateway, with whatever real key that program holds.

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

What exists today:

- **Releases are built by CI from the tag, never on a workstation** (ADR 0018). A `v<version>` tag that
  names the build's version runs the whole CI suite against the tagged commit. The Windows and Linux
  packages that run builds and smoke-tests, and the image it builds and runs, saved as an archive and run
  again from it, assembled into one artifact by a read-only job that checks nothing out (ADR 0021,
  ADR 0022), are what the release carries; nothing is rebuilt for it. Each artifact is
  taken by the ID the job that uploaded it reported, so one that another job of the run re-uploaded
  under the same name is refused rather than published. The package builds restore no dependency
  cache: what they bundle comes from the lockfile and the registry, never from a store another run
  wrote.
- **Releases are drafts until a maintainer publishes them.** The workflow creates a draft pre-release,
  and a person reviews it before it becomes visible. Release immutability is on: once published, a
  release's assets and tag cannot change, and `gh release verify` checks GitHub's attestation of them.
- **The image reaches the registry only when a person publishes a release** (ADR 0022). The release
  carries the image CI built and tested as an archive. Publishing it starts a workflow that refuses a
  release GitHub Actions did not create or that is not immutable, checks the archive against the
  release's `SHA256SUMS` and against the digest GitHub recorded at upload, reads what the archive holds
  before loading it, pushes it under the release's version — never `latest` — and refuses to move a
  version tag that already names another image, stopping rather than pushing when the registry's answer
  leaves that open. No other job of any workflow can write to the registry, and a contract test holds
  every workflow to that, so no workflow on `main` reaches it when a tag is pushed. A tag runs the
  workflows of the commit it names, so only reviewed commits on `main` are tagged: anyone with write
  access to the repository could push a commit whose workflow asks for `packages: write`, and that access
  is the boundary.
- **Each job that writes, writes one kind of thing,** and none checks anything out or installs anything,
  so no dependency's install script runs with a write token in reach: the release's `publish` job
  creates the draft (`contents: write`), `publish-image.yml`'s `push` job pushes the image
  (`packages: write`), and its `notes` job writes the image's digest into the published notes
  (`contents: write`). Every job that builds holds read access only, and no workflow step puts an
  expression into a script.
- **Digests.** Each release carries one `SHA256SUMS` for all its assets, which CI verifies when it
  assembles them, the publishing job verifies again before creating the release, and the installation
  guide tells users to check by the line for the file they downloaded. GitHub shows each asset's digest
  too. These detect corruption in transfer; they cannot detect a substitution by whoever wrote both the
  archive and its line, which is why the artifacts are taken by ID and why signing and attested
  provenance remain follow-ups.
- **Pinned inputs.** Package versions are central and exact (`Directory.Packages.props`), the dashboard
  installs from a frozen lockfile with an exact `pnpm`, and the collector image is pinned by version.
  Base images are pinned by tag, not by digest.
- **Known advisories fail the build.** Restore audits the packages `Directory.Packages.props` names
  against NuGet's advisory database, and with warnings as errors an advisory against one fails every
  build, the release's among them. The build does not audit transitive packages (ADR 0024).
- **Run by digest.** A tag in a registry can be moved by whoever can push to the package; a digest
  cannot. The release notes end with the image's reference by digest, whose manifest the workflow fetched
  by that digest and checked against the archive, and the installation guide shows how to run by it. The
  notes stay editable after publication, so the anchor is the release's own copy of the image, immutable
  once published: `gh release verify-asset` checks it before `docker load`.
- **No unreviewed dynamic plugin loading.**

Not yet:

- automated dependency updates with review;
- SBOM generation;
- vulnerability scanning of transitive packages, of the dashboard's dependencies, and of the image's
  operating-system layer;
- signed release artifacts — neither the packages nor the image are signed, and no build provenance is
  attested;
- a tag ruleset limiting who may create `v*` tags: until then, write access to the repository is what
  decides who can start a release;
- provenance for community adapters and profiles, which arrive with the stages that add them.

## Disclosure

Vulnerabilities are reported privately through GitHub's private vulnerability reporting, which is
enabled for the repository; root `SECURITY.md` says how, and what a report should include.
