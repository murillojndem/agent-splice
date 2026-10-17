# Testing strategy

## Test projects

- Unit tests: domain rules, provenance, timelines, sanitizers, parsers, policies, and later adapters.
- Contract tests: public protocol and administrative API behavior.
- Integration tests: API plus fake upstream plus persistence.
- Architecture tests: dependency and adapter boundaries.
- Performance tests: gateway overhead, streaming allocation, and bounded queues.
- Conformance tests: versioned protocol and behavioral suites.
- Evaluation tests: scenario orchestration with deterministic small fixtures.

## Required Stage 1 fixture families

### OpenAI requests

- minimal chat;
- system/user/assistant messages;
- unknown fields;
- tools passed transparently;
- `tool_choice` passed transparently;
- streaming options;
- malformed input;
- model alias resolution;
- stable errors.

### SSE

- one event per read;
- event split byte by byte;
- UTF-8 character split across reads;
- multiple events per read;
- multiline data;
- comments/keepalive;
- CRLF and LF;
- malformed JSON;
- premature EOF;
- `[DONE]`;
- usage terminal chunk;
- duplicate terminal event;
- a runtime that stalls after the terminator;
- an event larger than its bound, both unterminated and completed in the same read;
- an oversized event behind the terminator in the same read;
- an event-stream content type carrying a `charset` parameter;
- a malformed content type that still names the right media type;
- a valid content type longer than the evidence bound;
- client disconnect;
- a client write that reports the client gone without throwing.

### Trace and privacy

- timeline event ordering;
- each boundary stamped at the operation it names, not at a shared instant;
- timestamps non-decreasing across the whole timeline;
- unknown timestamps remain absent;
- measured/reported/estimated provenance;
- body retention disabled by default;
- no prompt or response in default logs;
- headers sanitized;
- persistence failure behavior;
- trace and request ID correlation.

## Later conformance fixture families

### Tool calls

- native structured call;
- multiple calls;
- nested arguments;
- invalid argument JSON;
- unknown tool;
- missing/duplicate IDs;
- streaming fragments;
- tool-result continuity;
- ordinary prose containing examples;
- Unicode;
- adversarial delimiters;
- profile-gated text adapters.

### Replay

- exact artifact creation;
- credential removal;
- deterministic placeholders;
- integrity hash;
- target allowlist;
- exact versus adapted manifest;
- comparison categories;
- cancellation and timeout;
- no tool execution.

### Protocol translation

- lossless supported mapping;
- explicit translation loss;
- role/content ordering;
- tool lifecycle;
- streaming lifecycle;
- unsupported vendor extensions.

## Fake upstream server

`tests/AgentSplice.TestSupport` provides `FakeUpstreamServer`: a real Kestrel listener on an ephemeral loopback port that answers with scripted responses and records every request verbatim.

It is a real listener rather than an in-memory handler because streaming preservation, cancellation propagation, and timeout phases are properties of the transport. An in-memory handler cannot demonstrate that a client disconnect reached the runtime, nor that events were flushed rather than buffered.

Capabilities:

- `UpstreamResponseScripts` for JSON, status-only, malformed, and truncated responses;
- `SseScript` for byte-exact event streams, with LF or CRLF endings, multiline `data`, comments and keepalives, named events, `retry` directives, and the `[DONE]` sentinel;
- byte-level rechunking, including one byte at a time, so events split across arbitrary network and UTF-8 boundaries;
- per-event delays, delayed headers, and trailing stalls for the response-header and idle-stream timeout phases;
- connection reset for premature EOF;
- `ReceivedRequests` for exact-forwarding assertions;
- `RecordedUpstreamRequest.WaitForAbortAsync` for upstream-side cancellation evidence.

Response resolution is path-specific queue, then shared queue, then default. The out-of-the-box default is HTTP 404, so a test that forgot to script a call fails loudly instead of receiving an accidental 200.

The fixture is itself tested in `AgentSplice.IntegrationTests`. Every streaming, cancellation, and timeout test depends on it, so a fixture that silently buffered or completed cleanly where it was told to reset would let those tests pass while the gateway was broken.

## Log and trace capture fixtures

`tests/AgentSplice.TestSupport` also provides `CapturingLoggerProvider`, which records every log
message, structured state value, and active scope. The formatted message alone is not enough to prove
content never leaks: a structured value or a scope property reaches a sink just as surely.

The privacy suite runs with logging at `Trace`. The claim worth making is that content is absent even
at the most verbose setting, not that the default level filters it out — the weak form of that test
passes on a gateway that logs prompts at `Debug`.

Integration collections run serially. `WebApplicationFactory` resolves a top-level-statements entry
point through static handoff state shared across the process, so concurrent hosts let one factory
observe another's disposed provider, and tests that deliberately fail startup then see
`ObjectDisposedException` instead of the validation failure they assert.

## Delivered by Stage 1A

From the fixture families above: minimal chat, system/user/assistant messages, unknown fields, tools
and `tool_choice` passed transparently, malformed input, model alias resolution, stable errors,
timeline event ordering, unknown timestamps remaining absent, measurement provenance, body retention
disabled by default, no prompt or response in default logs, headers sanitised, and trace/request ID
correlation.

## Delivered by Stage 1B

Every SSE family: one event per read, an event split byte by byte, a UTF-8 character split across
reads, multiple events per read, multiline data, comments and keepalives, CRLF and LF, malformed
JSON, premature EOF, `[DONE]`, the usage terminal chunk, a duplicate terminal event, and client
disconnect. Each is asserted twice — once against the frame reader in isolation, and once end to end
through the gateway with an independent client-side parser.

## Delivered by Stage 1C, part 1

Persistence-failure behaviour, which Stage 1B recorded as still owed. It is asserted through a context
factory that refuses rather than a real database made to fail: breaking a file mid-run is
timing-dependent and platform-specific, while the property under test is the policy — the failure is
logged with a stable event ID, counted per exchange lost rather than per batch, the batch is dropped
rather than retried, the writer keeps draining, and nothing reaches the caller that produced the
evidence.

Alongside it: the row mapper's absence rules (an unobserved boundary produces no row, unreported usage
stays null rather than becoming zero, an estimate is never stored as measured), queue saturation
counted rather than silently dropped, and end-to-end assertions that a real proxied request reaches a
real SQLite file with no prompt or response content anywhere in it.

PostgreSQL Testcontainers are not used yet, because no PostgreSQL provider ships. They arrive with the
provider, and the SQLite tests stay regardless.

### Added by the Stage 1A/1B correctness reviews

The per-event bound is asserted at every shape that reaches it — unterminated, completed in the append
that crosses the ceiling, exactly on it, split across reads, and many small events whose total exceeds
it — because a bound enforced in the ordinary case and skipped in the interesting one looks exactly
like a bound that works. The interaction between the bound and the protocol terminator is asserted in
both directions: a violation behind `[DONE]` does not retract the completion, and one ahead of it
still ends the stream.

Media-type classification is asserted against the RFC 9110 grammar rather than against a list of
strings that happen to work, including the malformed values that name the right media type
(`text/event-stream; ===`) and the empty-parameter production the grammar permits
(`text/event-stream;`).

### Stage 1B fixture additions

`SseScript.Gate(UpstreamGate)` stops a scripted response at a chosen point until the test releases
it. Chunk delays are real wall-clock waits, so a test built on them is either slow or flaky and never
both; a gate replaces the guess with a fact, which is what makes per-event delivery, mid-stream
disconnect, and idle-timeout tests deterministic. It cannot be combined with re-chunking, because
re-chunking destroys the boundary the gate was placed at.

`SseScript.RawBytes` expresses a payload that is not valid UTF-8. A relay that decoded text cannot
forward those bytes unchanged, and a fixture that can only express strings cannot test whether it
does.

`SseClientReader` parses the gateway's output from the client's side. It is deliberately an
independent implementation rather than the gateway's own `SseFrameReader`: a test whose parser is the
subject's parser proves only self-consistency and keeps passing through any framing bug the two
share. It is the same principle that keeps the fake upstream free of production types.

A scripted response carrying both a whole body and a sequence of chunks is now refused. Previously
both were written, so a test that took a whole-body helper and added chunks received a payload it
never intended, with nothing to say so.

### Proving when a boundary was stamped

A timing boundary is a claim about *which operation* read the clock, and no test that runs against a
socket can pin that down: the fixture cannot make time pass between the first byte of a body and the
last on demand, and a test that waited for a real delay would be racing the reader rather than
asserting on it. Two of the five defects ADR 0010 corrects survived a green suite for exactly this
reason.

The boundary tests therefore drive the relay and the provider directly, with a controllable
`TimeProvider` and fakes that advance it *inside* the operation they model — a scripted upstream body
whose read costs twenty seconds, a client sink whose flush costs five. A boundary stamped before the
await and one stamped after it are then a wall apart rather than microseconds apart, and no
accidental pass can squeeze through. The clock also auto-advances on every reading, so two boundaries
can never share a timestamp by coincidence.

Where a boundary is asserted end to end, it is asserted as an ordering or an absence — never as an
approximate duration.

### Replaced Stage 1B contracts

Two Stage 1B tests asserted the defective behaviour and were rewritten rather than extended:

- the boundary-order test expected the decoded-event boundary before the client-flush boundary, which
  contradicted the relay's own write-then-decode structure;
- the duplicate-terminator test required both terminators to be relayed and three events recorded,
  which consolidated a runtime anomaly as the contract.

A test that encodes a defect is worse than a missing test: it makes the defect look deliberate.

## Delivered by Stage 1D, part 2: the dashboard served by the gateway

The serving rules are integration tests against the production host (`tests/AgentSplice.IntegrationTests/Dashboard`),
and they never depend on a real dashboard build: whether one exists on a machine, and what its hashes
are, would make them pass or fail by accident. `TemporaryDashboardBuild` writes the shape the gateway
checks for — `index.html` and an `assets` directory — plus a hidden file inside it and a file beside
it, each holding a sentinel a response body is searched for. Path traversal goes through
`TestServer.SendAsync`, because `HttpClient` normalises dot segments before sending.

The central guard compares every answer outside `/dashboard` with the same request to a gateway that
serves no dashboard, and fails against a root `MapFallbackToFile`. The two build systems' shared facts —
Vite's `base` and `assetsDir` against the gateway's mount and cached directory, the Dockerfile's copy
target against the shipped `rootPath`, and `.dockerignore`'s patterns — are file-reading contract
tests, because a drift in any of them produces an image that starts cleanly and shows a blank page or
no dashboard at all. The dashboard's own `tests/mount.test.tsx` renders the real root under Vitest,
which applies the same `base`.

What only the real artifact can prove — that the image carries the dashboard it was built with, under
real Kestrel path normalisation — is the CI `image` job: it builds the image through Compose and runs
`.github/scripts/smoke-gateway.sh` (then named `smoke-image.sh`) against it. Those measurements come
from a container on a CI runner or a workstation, not from the fake upstream, and make no performance
claim.

## Delivered by Stage 1D, part 3: the image reports its health

The probe is tested in `tests/AgentSplice.IntegrationTests/HealthProbe` against a real loopback
Kestrel listener — the fake upstream — wherever the property is about the transport: a refused
connection, a redirect it must not follow, a stall. Its three-second budget runs on an injected
`FakeTimeProvider`, so the stall is expired by advancing the clock rather than by waiting it out. One
test runs it against the gateway itself through `TestServer`: liveness answers `204` and `HEAD` with
`405`, so a probe that demanded exactly `200`, or asked with `HEAD`, would report a healthy gateway as
unhealthy while every scripted check still passed.

The Dockerfile's `HEALTHCHECK` is pinned by file-reading contract tests
(`ImageHealthCheckContractTests`): exec form, liveness not readiness, a target the probe's own parser
accepts, the port the image's `ASPNETCORE_URLS`, `EXPOSE`, and Compose all name, the path the image
copies the probe to, a probe budget shorter than Docker's `--timeout`, and no Compose override. An
architecture test holds the probe to the base class library and keeps every gateway assembly from
referencing it.

What only the running image can prove is the CI `image` job: `docker compose up --wait` returns only
once the image's health check reports healthy, and the job then checks the reported status and that
the probe exits exactly `1` against a port nothing listens on and against a target it must refuse.

## Delivered by Stage 1D, part 4: a Windows package that needs no .NET SDK

The content-root decision is a pure function of the runtime switch, the host settings, and the
executable's directory, so `ContentRootDefaultTests` tests it as one: off by default, the executable's
directory when on, and an explicit content root — on the command line in each form the configuration
provider accepts, or from the environment — winning even then. The test process is asserted not to
carry the switch, because every other integration test depends on the ordinary rule.

`SqliteStoreLocationTests` pins which store paths resolve against the content root and which are
returned as the same string instance: `:memory:`, `Mode=Memory`, `file:` URIs, `|DataDirectory|`,
absolute paths, and an empty data source. `StoreLocationTests` boots the host with a relative path
that names different files from the content root and from the working directory — the technique the
dashboard's `rootPath` test uses — and fails against a raw `UseSqlite`. A missing store directory is a
startup failure naming the file (`HostStartupTests`). `WindowsPackageContractTests` —
`SelfContainedPackageContractTests` since part 8 — reads the publish
profile and fails if the switch's name drifts from the gateway's constant, if any package setting
moves into the project file, or if a self-contained profile lacks the switch.

What only the real package can prove is the CI `package-windows` job, on `windows-latest`:
`.github/scripts/smoke-windows-package.ps1` — `smoke-package.ps1` since part 8 — checks the archive
against `SHA256SUMS` and what it
contains, then starts it from an empty folder elsewhere — with the shipped defaults, where it reads
back a refused request from its store, proving EF Core and the native SQLite library inside the single
file, and with a token, where it runs `smoke-gateway.sh`, whose backslash traversal vectors only a
Windows file system can test. The same script, run against a package published without the switch,
fails before the first request. As with the image, these runs come from a runner or a workstation and
make no performance claim.

## Delivered by Stage 1D, part 5: tagged releases as reviewed drafts

The release workflow runs only when a tag is pushed, so `ReleaseWorkflowContractTests` reads it on
every build instead:

- a release is created only with `--draft`, `--prerelease`, and `--verify-tag`;
- only the publishing job may write, and it checks nothing out and installs nothing;
- the artifact it publishes is the one `ci.yml`'s `package-windows` job uploads, and `ci.yml` is
  callable;
- only a `v*` tag starts it, and the tag is checked against the build's version;
- the dashboard's `package.json` carries the gateway's version.

Each fails against its reintroduced defect. The workflow itself is proven only by the first real tag,
whose output is a draft that can be deleted.

## Delivered by Stage 1D, part 6: sample client configurations and a quickstart

The sample and the client guide are files a user copies, so the tests read them the way a client would
and compare them with the gateway's own constants. `ClientSampleContractTests` lives in the
integration project because the address is `LoopbackBindingDefault.Urls`, and the contract project does
not reference `AgentSplice.Api`:

- the OpenCode sample's `baseURL` is the gateway's loopback default plus `/v1`, and its package is
  `@ai-sdk/openai-compatible`;
- it defines only the gateway's provider, and every `model` and `small_model` in it, at any depth,
  names a model under that provider;
- it carries no `apiKey`, no `headers`, no provider `env` list, and no `{env:}` or `{file:}`
  substitution;
- no guide or sample names the gateway's port on `localhost` or the IPv6 loopback, and every loopback
  address the README and the client guide name is the gateway's, or the shipped runtime's on a line
  about LM Studio;
- the client guide names the exchange header, and every `x-agentsplice-*` header the guide or the
  README names, in any case, is one `GatewayHeaderNames` declares.

In the contract suite, `ConfigurationKeyContractTests` reads every setting the user guides name — as an
environment variable or as a `--agentsplice:` argument, whole, through single underscores and in any
case — and resolves it to a value rather than to a section, with a guard on the extraction itself.
`ErrorCodeContractTests` checks every code the user guides name against the codes the gateway can emit.
Both read the guides from one list, `Documents/UserGuides`.

Each fails against its reintroduced defect. What only real clients can show — that the commands work
as written in each shell, and that OpenCode reaches a model through the sample — was checked by hand on
a workstation against LM Studio, which makes no compatibility claim. So was a gap the in-process host
cannot show, because it sends headers as soon as they are committed: under Kestrel, a stream that times
out after the runtime's headers but before its first byte ends with no response at all (ADR 0019).

## Delivered by Stage 1D, part 7: the license and the reporting channel

A license is published once and read by everyone who receives an artifact, so `LicenseContractTests`
reads the files every build and holds them to one another (ADR 0020):

- `LICENSE` is the Apache License 2.0 word for word: its SHA-256, with LF line endings, is the one of the
  text the Apache Software Foundation publishes;
- exactly one file at the root has a name GitHub's license detection reads, and there is no `LICENSES`
  directory, so GitHub can name the repository's license;
- `package.json`, the OpenAPI document's `info.license`, and the image's
  `org.opencontainers.image.licenses` label name the same SPDX identifier;
- the image's source label is the build's `RepositoryUrl`, and `SECURITY.md` links that repository's
  private vulnerability reporting;
- the image's description fits the 512 characters GitHub's package page shows;
- `NOTICE` names the copyright holder the build declares;
- every package in the gateway's restored graph that puts a runtime, native, or resource file into the
  build — read from `src/AgentSplice.Api/obj/project.assets.json`, so the solution must have been restored
  — and every package in the dashboard's production graph, walked through `pnpm-lock.yaml`, is in the
  Packages column of `THIRD-PARTY-NOTICES.md`'s Components table, exactly or by a `.*` family, or in its
  list of packages that do not ship, and not in both. A name elsewhere in the file counts for nothing.
  Guards on the extraction check that both graphs include transitive packages, that the gateway's leaves
  out what ships nothing — so the set is the same after a single-file publish's restore — and that the
  dashboard's excludes development tools;
- the dashboard's source imports only packages its `package.json` declares under `dependencies`, which
  is what makes the production graph the one the bundle is built from;
- the image copies `LICENSE`, `NOTICE`, and `THIRD-PARTY-NOTICES.md` into its runtime stage, the package
  script copies them and refuses to build without them, and the release's `version` job refuses to run
  without them.

`IssueFormContractTests` holds every issue form to the rules of GitHub's form schema whose breach makes
GitHub drop it — a name, a description rather than a Markdown template's `about`, elements of a known
type, labels, markdown values, distinct dropdown choices, and distinct well-formed IDs — and to labels
GitHub creates in every repository. It also requires the bug form to ask for the AgentSplice version and the installation, and
the chooser to link vulnerabilities to private reporting. In CI, the package smoke test checks that the
archive carries the three files, identical to the repository's, and the bundled runtime's own notices
in `licenses/`; the image job compares the running image's copies with the repository's and reads its
labels back.

Each fails against its reintroduced defect, listed in the changelog.

## Delivered by Stage 1D, part 8: a Linux package

The Linux package is the Windows package's twin, so the tests hold the two to one set of rules rather
than repeating them (ADR 0021). `WindowsPackageContractTests` became `SelfContainedPackageContractTests`:

- the self-contained publish profiles are exactly the platforms a package is built for, `linux-x64` and
  `win-x64`;
- over each: self-contained, single-file, untrimmed, uncompressed, not ReadyToRun, native libraries
  beside the executable, no IIS assets, the content-root switch, and no development settings;
- the package script accepts exactly those platforms, its `dotnet publish` takes the profile named for
  the one it is given, and no profile is named anywhere by a literal;
- nothing package-specific is declared in the project file or `Directory.Build.props`, as before.

`ReleaseWorkflowContractTests` replaced its check of the one Windows artifact with three:

- every platform the package script accepts has a CI job that builds it, runs the smoke test, uploads
  `agentsplice-<platform>`, reports the artifact's ID, and restores no dependency cache, and no job or
  step there can be skipped or allowed to fail;
- `release-assets` takes every `agentsplice-*` artifact by the ID its job reported and none by name,
  needs every job that uploads one, uses no action but the artifacts' own, installs nothing, verifies
  strictly, uploads `agentsplice-release` from `release/`, and reports its ID, which `ci.yml` reports to
  the release;
- the release takes that artifact by ID and nothing else, verifies the digests before it creates the
  draft, checks every platform's archive, and creates the draft from what `SHA256SUMS` lists.

What only the real package can prove is the CI `package-linux` job, on `ubuntu-latest`, running the same
`smoke-package.ps1` the Windows job runs. It runs the installation guide's own `sha256sum` check, then
reads the archive's listing before extracting it: every entry owned by 0:0, folders and the executable
`rwxr-xr-x`, every other file `rw-r--r--`. Then it holds the Linux package to every check the Windows one
meets, finding the listener with `ss` where Windows uses `Get-NetTCPConnection`. Both platforms now also
check the README's line endings and match the whole `Content root path:` log line. The `release-assets`
job assembles and verifies a release's assets on every change, so on the release's first real run only
the draft is new. The assembly and publishing scripts were also run against real archives before CI,
refusing a wrong tag, an extra file, and a stray file in an artifact.

Each fails against its reintroduced defect, listed in the changelog.

## Delivered by Stage 1D, part 9: the image is published when a release is

A published image cannot be taken back, and `publish-image.yml` runs only when a person publishes a
release, so `ImagePublicationContractTests` reads the workflows on every build instead (ADR 0022):

- `publish-image.yml` runs on a published release and on dispatch with a required tag, and on nothing
  else;
- every workflow declares its permissions, and no job of any workflow but `publish-image.yml`'s `push`
  can write packages or runs a registry command such as `docker push`;
- its default is read, its only jobs are `push`, with `contents: read` and `packages: write`, and
  `notes`, with `contents: write`, which runs after it;
- no job of it declares an action, a container, or services, and no step uses an action, installs or
  downloads anything, or builds, runs, pulls, or tags an image;
- the `push` job refuses a release that is not published, not immutable, or not made — with its image —
  by GitHub Actions, and downloads the archive, verifies it against `SHA256SUMS` and GitHub's record, and
  reads its manifest before it loads it;
- it pushes once, only when the lookup answered that the tag is absent or unreadable, refuses to move a
  tag, stops on any other answer, and names the digest whose manifest it fetched and hashed;
- no step of any workflow puts an expression into a script, and no value in any workflow names `latest`;
- the jobs that build, load, and push the image run on `ubuntu-24.04`, and the `image` job stops on the
  containerd image store before it builds;
- the `image` job builds once, with `docker build --provenance=false --sbom=false`, starts with
  `--no-build`, smoke-tests, and only then saves the running container's image, refusing Compose's
  labels;
- Compose's image has no account namespace and is built, never pulled, and CI builds it under that name;
- the bash and the PowerShell `docker run` in the installation guide are, word for word, the one
  `image-archive` runs but for the image they name; every port a document's `docker` command or Compose
  publishes is on loopback; and every image name a document gives is the repository's;
- `release-assets` needs `image-archive` and expects the image's archive, and so does the step of the
  release's `publish` job that verifies the draft; every step that makes, loads, or pushes the archive
  spells its name and the image's the same way;
- every job `release-assets` needs, `image` and `image-archive` among them, can be neither switched off
  nor allowed to fail, and the release's `publish` job holds `contents: write` and nothing more.

What only Docker can prove runs in CI on every change: the `image` job saves the image it checked, and
`image-archive` loads that archive on another runner, checks its manifest, labels, and platform, runs it
with the guide's `docker run`, smoke-tests it, and checks that the store is on the volume. The push itself
runs only at a release; before this change it was rehearsed against a local registry, where it pushed,
left an unchanged tag alone on a second run, and refused to move a tag that named another image. Its
lookup, its check of what it pushed, and the `notes` job were also run against a fake registry and a fake
`gh`: it refused to push while the registry failed and the tag named another image, refused a tag that
moved after the push, and edited the notes once over repeated runs, and not at all when they already
named the digest.

Each fails against its reintroduced defect, listed in the changelog.

## Delivered by Stage 1D, part 10: the first dated trace report

A trace report is written once, by hand, from a machine CI never sees, so `TraceReportContractTests`
reads every report under `docs/reports` on every build instead (ADR 0023):

- at least one report is published (SPECIFICATION section 22, criterion 17), and the index has one row
  per report, giving its date, client, runtime, and models as `report.json` does;
- each report is exactly `README.md`, `report.json`, and `exchanges.json`, named for the UTC day of its
  first attempt and for its client and runtime, with an optional `-<n>`; the time it covers is its
  attempts', every timestamp states its time zone, and the evidence was exported after the sessions;
- the evidence's top level and each exchange hold only the API's answers and the export time; it holds
  every exchange its own list names, once; system information, every exchange, every measurement, and
  every observation has what the OpenAPI document requires; statuses, terminations, failure classes,
  protocols, units, observation types, and sources are the domain's; usage keeps its provenance; each
  timeline runs in sequence from `request_accepted` at the exchange's start to `persistence_completed`,
  or `metadata_queued` where the completion stamp failed; and every exchange is `metadata_only`;
- no key in the evidence is on a list of names an API puts content under, and no value holds four
  consecutive words of the scenario's prompt or a file it names;
- every exchange belongs to exactly one attempt, inside that attempt's time, asked of its model and
  served by a model the environment describes;
- every value of the environment and the scenario has a source that names one, none is empty, an
  unknown one has a source long enough to give a reason, the environment gives the values
  `docs/reports/README.md` lists, its version and content capture are the evidence's, its commit is the
  version's build metadata when there is any, and its package digest is a digest;
- the report says that it makes no compatibility claim, names its commit, has its sections, and its
  exchanges table's Exchange column names every exchange once;
- no file of a report holds a path under a home directory, in any case, with or without a drive, a
  bearer token, a 64-digit value that is not a declared digest, a session marker, or, outside CI, the
  account or machine name of whoever runs the test;
- every row of the compatibility matrix holds only untested values while no conformance report exists.

What no program can check stays with the author and the reviewer, and `docs/reports/README.md` says so:
that the export covered every exchange the sessions produced, that no failed attempt was left out, that
no value was guessed, that no other content reached an evidence value, and that prose names nobody.

What only a real machine can show was the report itself: OpenCode 1.18.7 ran a small coding task through
the Windows package CI built, to LM Studio 0.4.20 with two models, four times, and the evidence is what
the gateway's administrative API answered. The markers in each attempt's prompt were searched for in the
gateway's log, its store, and the evidence, which held none of them, and in LM Studio's own log, which
held the three that were sent.

Each fails against its reintroduced defect, listed in the changelog.

## Delivered by Stage 1D, part 11: the stage the gateway reports

`ImplementedStageContractTests` holds `DiagnosticsService.ImplementedStage` to the one stage
`docs/ROADMAP.md` marks in progress, and `DiagnosticsApiTests` checks that `GET /api/v1/system` reports
that constant. The stage had been a literal pinned by a test, and stayed `1C` through Stage 1D until the
first trace report read it.

Each fails against its reintroduced defect, listed in the changelog.

## Delivered by Stage 1D, part 12: .NET 10 LTS

`ToolchainContractTests` reads `global.json`, `Directory.Build.props`, every MSBuild file, the
Dockerfile, and every workflow, `.yml` or `.yaml`. It fails unless the target framework, both .NET
images, and every `actions/setup-dotnet` step name the version `global.json` pins, unless no project file
sets its own target or language and the language stays C# 12, and unless THIRD-PARTY-NOTICES.md names the
image the gateway runs on. `LicenseContractTests` holds the image's version label empty, and the image
job refuses an `org.opencontainers.image` label the Dockerfile does not declare.

The rest covers what .NET 10 changed in hosting, configuration, and Entity Framework Core:

- `MetadataPersistenceFailureTests`: a writer stopped before its loop ever ran still writes what was
  queued, and stopping it waits no longer than the shutdown token. A start token that is already
  cancelled is what makes that ordering deterministic, and a store that never answers is what shows the
  bound. The tests of the loop itself wait until the store has been asked for every batch before they
  stop the writer, because a stop straight after `StartAsync` would usually reach a loop that had not
  run yet.
- `RetentionSweepTests`: the first sweep has finished when `StartAsync` returns, shown with a store that
  answers only when the test lets it.
- `MigrationLockTests`: a lock row left in a real SQLite store fails startup once the bound passes on the
  injected clock, with an error naming the store and the lock, and a store without one starts with the
  clock never moved.
- `NullSettingValidatorTests`: settings read from JSON, because that is where a null comes from; a
  value-typed setting given as null fails startup by name, and the validator leaves a null string,
  section, or list alone, so a null section or list keeps its defaults and a required string is refused
  by the options validator.
- `AgentSpliceOptionsValidatorTests`: the retention windows and the sweep interval at, below, and above
  their bounds.

Each fails against its reintroduced defect, listed in the changelog.

## Testcontainers

Use PostgreSQL Testcontainers for persistence integration tests. SQLite tests remain required because behavior differs. Evaluation containers must use independent fixtures and explicit resource limits.

## Golden tests

Reviewed golden files may cover:

- protocol envelopes;
- SSE event sequences;
- trace timelines;
- sanitization reports;
- replay manifests;
- conformance reports;
- optional adapter output.

Golden updates require explicit review and must never be accepted automatically merely because output changed.

## Property and fuzz tests

Use bounded property/fuzz tests for SSE framing, JSON extension preservation, secret redaction, tool-call candidate parsing, and malformed input. Fuzz tests must have deterministic seeds in CI.

## Performance regression gates

Shared CI should gate gateway-only allocation and relative overhead against the fake upstream, not real-model latency. Hardware benchmarks run in labeled environments and publish raw results. Correctness regressions take precedence over throughput gains.
