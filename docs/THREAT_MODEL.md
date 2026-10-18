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
- host/GPU metadata — published in trace reports, by the maintainer's choice (ADR 0023);
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
11. Release pipeline to the operator's machine.
12. Container registry to the operator's machine.

## Threats and mitigations

### Unauthorized inference or diagnostics access

Threat: service bound to LAN/internet without adequate authentication.

Mitigations: loopback default; startup refuses a reachable binding without an administrative token, and counts `Kestrel:Endpoints` and `FORWARDEDHEADERS_ENABLED` as reachable; the token protects `/api/v1` from every caller once configured; concurrency limits.

Residual: `/v1` authenticates no one on any binding, as an OpenAI-compatible endpoint for local clients does, so a gateway a network can reach runs completions on the configured runtimes for whoever reaches it, and adds their exchanges to the store. Startup says so as event `1205` at Warning, and a deployment that needs `/v1` closed puts it behind its own authenticating proxy (ADR 0027).

### Credential confusion

Threat: client credential forwarded to an unintended runtime or persisted in evidence.

Mitigations: runtime-scoped secret references, header allowlists, no default pass-through, sanitization before persistence/export. The client's own credential is dropped at ingress, so the sample client configuration carries none and the client guide gives a placeholder, never the administrative token or a runtime key, which would otherwise sit in a client's settings file — for OpenCode, often a file in a repository (ADR 0019).

### Traffic that bypasses the gateway

Threat: a client pointed at AgentSplice still sends some requests elsewhere — to another provider it is configured with, to an auxiliary model for titles or summaries, or as its own telemetry — so the recorded evidence reads as a whole session when it is not, and content reaches a destination the operator believed was out of the loop.

Mitigations: the sample OpenCode configuration routes both `model` and `small_model` through the gateway, and a contract test holds it; the client guide says that other providers configured in a client remain reachable and unobserved, and sets `OPENAI_BASE_URL` per session rather than for the account, where it would redirect every OpenAI SDK program on the machine. Residual: AgentSplice sees only what is sent to it and cannot detect what a client sends elsewhere (ADR 0019).

### SSRF and arbitrary targets

Threat: attacker controls runtime or replay target URL.

Mitigations: administrative configuration only, URI validation, target allowlists, network policy, no per-request arbitrary base URL.

### Prompt/source leakage

Threat: logs, traces, metrics, dashboard, replay, or issue bundles expose private content.

Mitigations: metadata-only default, bounded attributes, content authorization, sanitization, retention, export review, automated leakage tests.

Status: trace reports are a published channel (ADR 0023). Their evidence is the administrative API's metadata, exported with content capture off; each attempt's prompt carries a marker that the author searches for in the gateway's log, store, and evidence, with the runtime's own log as the control that the search works; and `TraceReportContractTests` rejects, in the evidence, a key on a list of names an API puts content under and a value holding four consecutive words of the scenario's prompt or a file it names, and, in every report file, a path under a home directory, a bearer token, a 64-digit value that is not a declared digest, a session marker, and, outside CI, the account and machine names of whoever runs the test. Residual: other content reaching an evidence value is caught by the author's marker search, not by the test, and a user or host name written into prose only when it is the test machine's own, so the author's review is the control for both.

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

Threat: a web page the operator visits sends requests to a gateway on `127.0.0.1`. DNS rebinding — a hostile hostname re-resolved to `127.0.0.1` — makes the page same-origin with the gateway, so it could read `/v1`, and `/api/v1` when no token is configured. Separately, any page can send a cross-site "simple" `POST` — plain text, a form, or a multipart body — without the browser asking first.

Status: closed for the default configuration (ADR 0027). When `AllowedHosts` is not set, only requests addressed to `localhost`, `127.0.0.1` and `[::1]` are served, and a rebinding page's requests carry its own domain as the `Host`, so they are refused with a `400` before anything answers; the refusal is logged as `NoAllowedHostMatched`. A completion body must be declared as `application/json`, which a page cannot send cross-site without a preflight that the gateway never answers with CORS headers; anything else is a `415`, forwarded nowhere and recorded nowhere. Residual: a deployment that sets `AllowedHosts` to `*`, or names a host a hostile page can resolve, gives the rebinding read back.

The Windows and Linux packages run in exactly this configuration by default — loopback, no token — on the machine where their operator browses the web, so `docs/INSTALLATION.md` recommends setting a token and shows how (ADR 0017, ADR 0021).

### Dashboard asset tampering

Threat: something able to write to the gateway's files replaces the dashboard's script, which then runs with every operator's token.

Status: residual. In the image the build sits under `/app`, which the process user owns, as it owns the gateway's own assemblies; making the dashboard alone root-owned would protect nothing, because the owner of a directory can replace its contents. Follow-up: a read-only `/app`.

The packages have the same property in their install folders, where whoever can write one can also replace the executable itself. The installation guide puts the folder under the user's own profile, because a folder created directly under `C:\` grants write access to every authenticated user (ADR 0017), and on Linux creates it with `mkdir -m 700`; the Linux archive records nothing writable by group or others (ADR 0021).

### Evidence at rest

Threat: the SQLite store, or a backup of it, is read by someone it was not meant for. It holds structural metadata and the identifiers `docs/SECURITY.md` lists as retained — never prompts or output by default, but enough to describe what was run, when, and against which model.

Status: residual. The store is unencrypted and protected by its file system permissions alone: the `/data` volume in the image, and the folder it is created in for the packages — beside the executable unless configured elsewhere, a relative path resolved against the content root so that it never lands in whatever directory the process was started from (ADR 0017). On Linux SQLite creates the file readable by whomever the umask allows, so the folder's mode is what protects it, and the installation guide sets the folders it suggests to mode 700 (ADR 0021). The folder protects the file only: with no administrative token, every account on the machine can read the same evidence from `/api/v1` while the gateway runs, so the guide recommends the token wherever other accounts exist. Follow-up: create the store with mode 0600 on Unix, so the file protects itself. Startup names the file (`1305`) so an operator knows what to protect. Retention bounds how long rows stay; it does not reach copies. Follow-up: an encryption decision when content retention exists (`docs/SECURITY.md` "Content retention").

### Release artifact integrity

Threat: an operator downloads a package that is not the one the tagged source builds — replaced on the way, built from something other than the tag, or published by a compromised build step.

Mitigations: the packages are built only by CI, from the tag, after the full suite passes, with no dependency cache restored, and a tag that does not name the build's version stops the release before anything is built. A read-only job that checks nothing out verifies each package and assembles them, with one `SHA256SUMS`, into the artifact the release publishes (ADR 0021). Each artifact is taken by the ID its producing job reported, so another job of the same run that re-uploads one under the same name — archive and digest together — makes the download fail rather than the substitute publish. The release is a draft until a maintainer publishes it. The job that creates it is one of three that hold a write token, each with a single permission and none checking anything out or installing anything (ADR 0022); it verifies `SHA256SUMS` again before uploading, and the installation guide tells users to verify the line for the file they downloaded (ADR 0018).

Status: residual. The packages are not signed and no build provenance is attested. The digests detect corruption in transfer, not substitution: a compromised step inside a package job writes the archive and its digest together, and `SHA256SUMS` sits beside the archives it describes, so anyone able to replace a release asset can replace both. GitHub's record of each asset's digest is computed from whatever was uploaded, so it is not independent of that either. Release immutability is on, so once a release is published its assets cannot be replaced, and GitHub's attestation of the release lets `gh release verify` say so; a draft can still be changed before a person publishes it. Follow-ups: signing and attested provenance.

### Registry image substitution

Threat: an operator pulls an image that is not the one the release reviewed — pushed from a tag nobody reviewed, moved later under the version tag, or fetched under a name somebody else controls.

Mitigations: no workflow on `main` reaches the registry except when a person publishes a release (ADR 0022), and a contract test holds every job of every workflow to that. The job that pushes holds `packages: write` alone, checks nothing out, installs nothing, and uses no action. It refuses a release that is not published, that is not immutable, or that GitHub Actions did not create: the release's author and its image's uploader must be `github-actions[bot]`, so an image a person uploaded to the draft is refused. It checks the archive against the release's `SHA256SUMS` and against the digest GitHub recorded at upload, reads the archive's manifest before loading it, pushes only the release's version, never `latest`, and refuses to move a version tag that already names another image; when the registry's answer leaves open whether the tag exists, it stops rather than push. The digest it writes into the release notes is the one whose manifest it fetched by that digest and checked against the archive, and the installation guide shows how to run by it. Compose's own image is `agentsplice:dev` and is built, never pulled: its old name was a Docker Hub account anyone could register.

Status: residual. A version tag remains mutable to anyone who can push to the package, and the image is neither signed nor attested, so the digest in the notes is what an operator can pin. The notes stay editable after publication, so that digest is a convenience, not an anchor: the anchor is the reviewed archive, immutable in the release, whose config the pushed image must match. A public package can never be made private again, and its versions can be deleted only while they have few downloads. Every workflow's token acts as `github-actions[bot]`, so the release's author proves that a workflow made it, not which one. Write access to the repository is the boundary: a tag runs the workflows of the commit it names, and anyone with that access can push a commit whose workflow asks for `packages: write`, which no check in this repository's own workflows can stop; tags go only on reviewed commits on `main` (`docs/DEVELOPMENT.md`). Follow-ups: attested provenance for the image, signing, and a tag ruleset limiting who may create `v*` tags.

### Unserviced bundled runtime

Threat: a .NET security fix ships, and installed packages keep running the vulnerable runtime, because a self-contained application carries its own copy and neither Windows Update nor a Linux distribution's updates patch it.

Status: residual. Packages are built in CI with the current .NET 10 SDK, so each one carries the latest runtime patch at the time it was built, and `README.txt` in the package names that version. A fix reaches an installation only through a new AgentSplice release that the operator installs; there is no update check. Microsoft supports .NET 10 until November 14, 2028: after that date there are no .NET 10 fixes for any release to carry, until AgentSplice moves to a supported .NET. AgentSplice left .NET 8 before its support ended on November 10, 2026, so that no release was built on an unsupported runtime (ADR 0024). The published image is the same: its .NET runtime and its Ubuntu 24.04 packages are those of `aspnet:10.0-noble` when CI built it, and only a new release's image updates them.

### Container health probe

Threat: the image's health check becomes a way to reach something other than the gateway — a request to another host, a proxy, or a redirect target — or a way to report health that is not the gateway's.

Mitigations: the probe takes its one target from the image configuration, never from a request, and refuses anything but an `http` URL whose host is a loopback IP literal — no name resolution, no user information. It uses no proxy, follows no redirect, reads the status line and headers only, and gives up after three seconds. It references only the base class library, nothing references it, and it runs as the image's non-root user under `/app`, so replacing it requires the same write access that replacing the gateway does. Its one line of output names a status or a failure class and never echoes a target it refused (ADR 0016).

### Misleading evidence

Threat: estimates are presented as measurements, unsupported runs are omitted, or a report overgeneralizes from one environment.

Mitigations: provenance, immutable raw results, environment snapshots, status taxonomy, unsupported-result retention, report validation.

Status: partly addressed. Trace reports exist (ADR 0023): every value of a report's environment gives its source, an unknown value says why, failed attempts are kept, and every report says that it makes no compatibility claim. `TraceReportContractTests` checks what a program can in a report's files — their shape and vocabularies, that the evidence holds every exchange its own list names, that every exchange belongs to an attempt inside its time, that every value has a source — while whether the export was complete, whether a value was guessed, and whether a failed attempt was left out rest on the author and the reviewer (`docs/reports/README.md`). Until a conformance report exists, the compatibility matrix holds only untested values. Conformance reports and benchmarks do not exist yet.

### Denial of service

Threat: oversized prompts, streams, trace volumes, replays, or evaluation jobs exhaust resources.

Mitigations: size/concurrency/retention limits, bounded queues, separate workers, backpressure, cancellation, per-endpoint quotas.

## Security test cases

Each case below says whether a test holds it today, so the list cannot be read as coverage it does not
have.

Held by a test that fails if it regresses:

- authorization and runtime secrets never appear in logs or traces;
- a setting that names an environment variable is never repeated in a startup error (ADR 0027);
- nothing is exported unless OTLP export is explicitly enabled, and exported spans and metrics carry no content;
- no span carries the caller's `tracestate`, and a runtime receives the gateway's `traceparent` and no `tracestate` or `baggage` (ADR 0027);
- content retention remains off after upgrade;
- an arbitrary upstream URL is rejected;
- giant or malformed SSE terminates safely;
- a request addressed to a name other than `localhost`, `127.0.0.1` or `[::1]` is refused before anything answers, unless `AllowedHosts` names it (ADR 0027);
- a completion body not declared as `application/json` is refused, forwarded nowhere, and recorded nowhere (ADR 0027);
- every dashboard response carries a policy forbidding inline script, `eval`, cross-origin connections, and framing;
- nothing outside the dashboard build directory, and no hidden file in it, is served;
- serving the dashboard changes no answer outside `/dashboard`;
- the health probe refuses any target off loopback `http` without sending a request, and exits only 0 or 1;
- on Windows, backslash forms of a traversal never serve a file outside the dashboard build;
- each package writes nothing to the directory it is started from, and listens on loopback only;
- the image reaches the registry only from a published, immutable release, by the one job of any workflow that may write packages, which installs nothing and never moves a tag;
- a release is created only as a draft, only by the release workflow's `publish` job, and only after its digests verify;
- a dated trace report includes provenance and omits secret values.

Stated, and not yet held by a dedicated test:

- database failure does not leak connection strings — the failure is logged with its exception, and no test searches that text for the connection string;
- the dashboard encodes all content — React renders strings as text and the policy forbids inline script, but no test renders hostile content.

Later stages, with nothing to test yet:

- an arbitrary replay URL is rejected;
- known secret patterns are removed before replay persistence;
- replay never invokes tools;
- evaluation cannot modify host fixture source outside the mounted workspace;
- an adapter cannot activate outside its declared constraints — today `agentsplice:adapters:enabled` is refused outright;
- exported issue bundles include provenance and omit secret values.
