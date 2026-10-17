# Changelog

All notable changes will be documented here.

## Unreleased

### Stage 1D, part 9: the image is pushed to GHCR when a release is published

The roadmap's "Docker image published to a registry" was open: a release's image had to be built from
its tag. A release now carries the image CI built and tested as an archive, and publishing the release —
not pushing the tag — pushes exactly that image to GitHub Container Registry (ADR 0022, superseding ADR
0018 decision 7).

- The CI `image` job, pinned to `ubuntu-24.04` and refusing the containerd image store, builds with
  `docker build --provenance=false --sbom=false` instead of through Compose, whose labels would have gone
  into a published image, starts it with `docker compose up --no-build`, runs every check it ran before,
  and then saves it, tagged `ghcr.io/<owner>/<repository>:<version>` from the version the gateway inside
  it reports, as `agentsplice-<version>-image-linux-amd64.tar.gz` with its `SHA256SUMS`.
- New CI job `image-archive` loads that archive on another runner, checks its manifest, labels, and
  platform, runs it with the installation guide's `docker run`, smoke-tests it, and checks that the store
  is on the volume. `release-assets` takes the archive by its artifact ID once `image-archive` has passed,
  so every draft carries it, covered by the same `SHA256SUMS`.
- New `.github/workflows/publish-image.yml`, on a published release and on dispatch with a tag. Its
  `push` job, with `packages: write` alone, refuses a release that is not published, that is not
  immutable, or that GitHub Actions did not create, checks the archive against `SHA256SUMS` and the
  digest GitHub recorded, reads its manifest before loading it, pushes the version tag only — never
  `latest` — refuses to move a tag that names another image, and stops rather than push when the
  registry's answer leaves that open. It then fetches the manifest by the digest the registry names,
  checks that its bytes hash to it and that its config is the archive's, and checks that it can be read
  anonymously. Its `notes` job, with `contents: write` alone, writes that digest into the release notes,
  editing them only when they do not already end with it. Neither checks anything out, installs
  anything, or uses an action, and no job of any other workflow can write packages.
- Release immutability is enabled for the repository: once published, a release's assets and tag cannot
  change, and `gh release verify` checks them.
- Compose's image is renamed `agentsplice:dev`, with `pull_policy: build`: the old
  `agentsplice/agentsplice:dev` belonged to a Docker Hub account anyone could register.
- `docs/INSTALLATION.md`'s Docker section now has the published image — `docker run` in bash and
  PowerShell, with the token exported in the shell that runs it, running by digest, the archive for
  `docker load`, upgrading — beside Compose from a clone. `docs/DEVELOPMENT.md`'s release runbook adds
  publishing, checking the package, tagging only reviewed commits, and what to do when `publish-image.yml`
  stops: a private package, a release that is not immutable, a registry that does not answer, or a defect
  in the workflow, fixed and run again through dispatch. `docs/SECURITY.md`, `docs/THREAT_MODEL.md` (a
  registry trust boundary and an image substitution threat), SPECIFICATION section 20.2, the roadmap, the
  README, and `THIRD-PARTY-NOTICES.md` (the image's Debian packages and where their source is) say what
  changed.
- No runtime behaviour changes.

Every new guard was verified to fail against a deliberately reintroduced defect:

- a push on release creation, a push trigger added, or the dispatch tag optional;
- `packages: write` at the top, or `contents: write` added to the push job;
- an action used, an image built, a load before the verification, two pushes, a moved tag, an
  expression in a script, or `latest` pushed;
- the push job on `ubuntu-latest`, or no containerd check;
- the image built by Compose, rebuilt on start, or saved before the smoke test;
- Compose's image under an account namespace, or pulled;
- the guide's `docker run` without the volume, or publishing on every interface;
- `release-assets` without `image-archive`, or taking the image by name;
- the release's verification no longer expecting the image's archive;
- another workflow's job granted `packages: write` or `write-all`, or running `docker push`; a workflow
  without permissions; such a workflow saved as `.yaml`; the release's `publish` job granted
  `packages: write`;
- every lookup failure taken for "not found", the lookup's error discarded, a failure that pushes, or the
  push outside the lookup's answers;
- the pushed manifest read by its tag again, or not hashed against its digest;
- the release's immutability, author, uploader, or GitHub's digest not checked;
- `latest` passed through `env`, a tag made anew, `--all-tags`, `imagetools create`, a job container or
  service, `apt`, `curl`, or `docker run` in the publishing jobs;
- the containerd check only warning, or made after the build; the saved image built anew;
- the archive's or the image's name spelled differently in one step;
- `image` or `image-archive` conditional, or one of their steps allowed to fail or made conditional;
- the guide's PowerShell command without the volume, either command with a flag CI does not use,
  `-p 5280:5280`, `-dp 0.0.0.0:5280:5280`, `-P`, or `--network host`; Compose publishing the gateway or
  PostgreSQL on every interface;
- the installation guide, the README, or the Compose file naming the image in another namespace.

The image's path was rehearsed locally against Docker Desktop and a local registry before CI ran it: the
save step, `image-archive`'s load, `docker run`, smoke test, and store check, and the push step's push,
its second run, and its refusal to move a tag. After review, the push step and the `notes` job ran against
a fake registry and `gh`, as they were at the first commit of this part and as they are now: the first
pushed over a tag that named another image when the registry failed, named the digest of a tag that moved
after the push, and edited the notes on every run; the second refused, failed, and edited them once.

### Stage 1D, part 8: a Linux package that needs no .NET installation

On Linux the only direct-process path was building from source. There is now a Linux package beside
the Windows one: the same self-contained single file, for x64 distributions based on glibc, built and
smoke-tested on Linux in CI and published by every release (ADR 0021).

- New `src/AgentSplice.Api/Properties/PublishProfiles/linux-x64.pubxml`, the Windows profile's twin:
  self-contained, single-file, not trimmed, native libraries beside the executable (`libe_sqlite3.so`),
  no development settings, and the content-root switch.
- `package-windows.ps1` becomes `package.ps1 -RuntimeIdentifier win-x64|linux-x64`, requiring PowerShell
  7.4. The Linux package is `agentsplice-<version>-linux-x64.tar.gz`, built on Linux only, with owner and
  group 0, sorted entries, and modes the script sets rather than keeps — the executable and the folders
  `rwxr-xr-x`, every other file `rw-r--r--` — because NuGet's files arrive with an execute bit. Its README
  has Unix line endings and commands, says to keep the folder to its owner with `chmod 700`, and, like
  the Windows one, says how to start it from any folder and to set a token on a machine other people
  use. A README links the guide at the release's own tag only when the build is that tag's.
- `smoke-windows-package.ps1` becomes `smoke-package.ps1`, which holds both packages to the same checks.
  On Linux it also runs the installation guide's own digest check, reads the archive's listing before
  extracting it — owners 0:0 and exactly the modes above — checks the execute bit once extracted, and
  finds the listener with `ss`. On both it checks the README's line endings and matches the whole
  content-root log line.
- New CI job `package-linux`, on `ubuntu-latest`, uploading `agentsplice-linux-x64`. Neither package job
  restores a dependency cache.
- New CI job `release-assets`, which takes both package artifacts by the IDs their jobs reported —
  never by name, which any job of the run can upload again with a digest to match — verifies each,
  combines their digests into one `SHA256SUMS`, checks that the set is one archive per platform of one
  version, and uploads `agentsplice-release`. It uses no action but the artifacts' own, checks nothing
  out, and runs on every change; `ci.yml` reports its artifact's ID to the release.
- The release's `publish` job takes `agentsplice-release` by that ID, verifies it, checks that its
  `SHA256SUMS` lists exactly this tag's archives, and creates the draft from the files it lists; the
  notes gain a Linux paragraph and say how to check a download against the combined `SHA256SUMS`.
- `docs/INSTALLATION.md` gains a Linux package section: checking the archive by its own line in
  `SHA256SUMS`, extracting into a folder set to mode 700, running it, and the token, configuration, and
  data, with two habits Windows does not teach — environment variable names are case-sensitive, and `~`
  is the shell's to expand. It says that the folder's mode protects the store's file but not what the
  gateway serves, and recommends the token wherever other accounts exist. Until the first release, it
  names only the artifacts of CI runs for a push to `main`, for Windows too: a pull request's run builds
  unreviewed code. The troubleshooting table covers what fails on Linux.
- The README, `docs/DEVELOPMENT.md`, `docs/SECURITY.md`, `docs/THREAT_MODEL.md`, SPECIFICATION section
  20.1, `docs/ARCHITECTURE.md`, `docs/OBSERVABILITY.md`, `docs/CLIENTS.md`, `THIRD-PARTY-NOTICES.md`, the
  bug form's installation choices, and the comments that described the Windows package alone now
  describe both. The security guide and the threat model say what the digests detect — corruption, not
  a substitution by whoever writes an archive and its line — and record creating the store with mode
  0600 as a follow-up. The guides and the threat model also state that Microsoft supports .NET 8 until
  November 10, 2026, after which no .NET 8 build receives runtime fixes.
- No runtime behaviour changes.

Every new guard was verified to fail against a deliberately reintroduced defect:

- the Linux artifact renamed; its smoke test dropped, switched off, or allowed to fail; its job
  reporting no artifact ID; a cache restored in a package job;
- a download missing from `release-assets`, one taken by name, a checkout, an action, or an install
  added to it, a `needs` entry missing, its verification no longer strict, or its upload nested;
- CI reporting another job's artifact to the release;
- the release taking its artifact by name, verifying after it creates, checking only the Windows
  archive, or naming its assets by hand;
- the script accepting only Windows, or publishing with a literal profile;
- the Linux profile deleted, trimmed, without the switch, or shipping the development settings.

The Linux package was built and smoke-tested in a Debian 12 container — the .NET 8 SDK image — before
CI ran it, and the release's assembly and publishing scripts were run against the real archives,
including the refusals: a wrong tag, an extra file, and a stray file in an artifact.

### Stage 1D, part 7: the project is licensed, and says where to report vulnerabilities

The repository was public with no license, so nothing it published could be used or passed on, and no
release could be cut: the release workflow refuses to run without a license. The maintainer chose the
Apache License 2.0, and what the project distributes now carries it and the notices of what it bundles
(ADR 0020).

- New `LICENSE`, the Apache License 2.0 as the Apache Software Foundation publishes it, byte for byte;
  `NOTICE`, naming the copyright holder; and `THIRD-PARTY-NOTICES.md`, listing every distributed
  component with its license and copyright. Its sources are the packages' own license files where they
  ship them, and otherwise the nuspec's license and `<copyright>` or the upstream repository's license. It
  carries the notices OpenTelemetry passes on, and a row for Vite, Rollup, and esbuild, whose generated
  code is part of the dashboard's bundle.
- `LICENSE-CHOICE.md` is removed: GitHub read it as a second license file and reported the repository's
  license as "Other". Its reasoning is ADR 0020's context.
- The Windows package carries `LICENSE`, `NOTICE`, and `THIRD-PARTY-NOTICES.md`, and in `licenses/` the
  .NET runtime pack's license and third-party notices and ASP.NET Core's third-party notices. The script
  asks the SDK which runtime packs the publish used, and refuses to build without any of the six files;
  the smoke test checks them.
- The image copies the three files into `/app` and gains `org.opencontainers.image` labels for its title,
  description, source repository, and license. CI checks the running image's copies against the
  repository's and reads the labels back.
- The release workflow's `version` job refuses to run without the three files.
- A contract test reads the gateway's restored dependency graph and the dashboard's production graph from
  its lockfile, transitive packages included and packages that ship no file left out, and fails for any
  package the notices' table does not list. Another checks that the dashboard's source imports only its
  production dependencies, which is what makes that graph the one that ships.
- `package.json`, the OpenAPI document's `info.license`, and the image's label name the same SPDX
  identifier. `Directory.Build.props` gains `Copyright` and corrects `RepositoryUrl`, which named a
  repository that does not exist.
- `SECURITY.md` names GitHub's private vulnerability reporting, now enabled, as the channel, and no
  personal address; the issue chooser links there too. `CONTRIBUTING.md` says that, under section 5 of the
  license, contributions come in under the same terms unless a contributor says otherwise, that
  contributions under other terms are not accepted, and that a new dependency is listed in
  `THIRD-PARTY-NOTICES.md` in the same change.
- **Defects fixed:** both issue forms carried `about`, the key of a Markdown template, instead of
  `description`, so GitHub offered neither, and both applied `needs-triage`, a label the repository never
  had. A bug report now also asks for the AgentSplice version and how it was installed.
- No runtime behaviour changes.

Every new guard was verified to fail against a deliberately reintroduced defect:

- a byte added to `LICENSE`, a second license file or a `COPYRIGHT` file at the root, or a `LICENSES`
  directory;
- `MIT` in `package.json`, in the image's label, or in the OpenAPI document;
- the old repository in the image's source label or in `SECURITY.md`'s link;
- an image description past GitHub's 512 characters, or another year in `NOTICE`;
- `scheduler`, `SQLitePCLRaw.*`, or the bare `OpenTelemetry` missing from the notices' table, or named only
  in prose; `js-tokens` missing from the packages that do not ship; a package in both;
- the dashboard importing a package it declares only as a development dependency;
- the image, the package script, or the release checking `LICENSE` alone;
- `about` back in an issue form, an unknown element type, a dropdown without choices, the version no
  longer required, `needs-triage` back, or the chooser linking vulnerabilities to public issues.

### Stage 1D, part 6: sample client configurations and a quickstart

Nothing showed how to point a client at the gateway beyond one sentence of the installation guide.
There is now a sample OpenCode configuration, a guide for OpenCode, Cline, and any client that speaks
OpenAI Chat Completions, and a README quickstart, and the address, headers, settings, and error codes
they name are checked against the gateway's own (ADR 0019, SPECIFICATION section 22 criterion 13).

- New `examples/opencode/opencode.json` defines an `agentsplice` provider with
  `@ai-sdk/openai-compatible`, at `http://127.0.0.1:5280/v1`, with no API key, and routes both `model`
  and `small_model` through it, so OpenCode's title requests stay on the gateway too. Its model is a
  placeholder that fails closed with `404` `agentsplice_model_not_found`.
- New `docs/CLIENTS.md` covers:
  - what every client needs, and why: the IPv4 loopback literal, a placeholder key, a model ID copied
    from `/v1/models`, two endpoints, and a context length large enough for an agent's prompt;
  - the OpenCode sample; Cline's OpenAI Compatible fields; `curl`, Windows PowerShell and PowerShell 7,
    the OpenAI Python SDK, and the SDKs' environment variables;
  - finding an exchange from `x-agentsplice-exchange-id`, and what a cancelled request records;
  - troubleshooting, including which timeout a slow request meets — a `504` while the gateway can still
    say so, a closed connection once a stream's status is committed — and a Compose override for the
    timeouts, which a line in `.env` does not reach.
- The README opens with a quickstart, says what the alpha does and does not do, and links the guide;
  `docs/INSTALLATION.md` links it too.
- **Documentation correction:** `docs/API.md`'s error table mapped every elapsed timeout to `504`. Once
  a stream's headers have arrived from the runtime the status is committed and the connection is
  abandoned instead, as the relay always did, and the table now says so. The specification's
  configuration example uses `127.0.0.1` rather than `localhost`.
- **Known gap, found and not fixed here:** Kestrel sends a committed status only with the first relayed
  byte, so a stream that times out or loses its runtime after the runtime's headers arrive but before
  its first byte — LM Studio processing a long prompt — ends with no response at all, not even
  `x-agentsplice-exchange-id`, while the exchange records the failure correctly. The guide and
  `docs/API.md` describe it, and the roadmap lists closing it as a request-path change for its own
  slice (ADR 0019).
- New `ClientSampleContractTests` reads the sample and the guides against `LoopbackBindingDefault.Urls`,
  the shipped runtime address, and `GatewayHeaderNames`. `ConfigurationKeyContractTests` now resolves
  every setting the user guides name as a variable or a `--agentsplice:` argument to a value, and
  `ErrorCodeContractTests` every error code they name; both read the guides from one list.
- `docs/THREAT_MODEL.md` gains "Traffic that bypasses the gateway" and extends "Credential confusion" to
  client configuration; `docs/SECURITY.md` gains a "Client configuration" section. The roadmap marks the
  sample client configurations done and lists the streaming gap as open. `tests/README.md` describes the contract and integration suites as
  they now are.

Every new guard was verified to fail against a deliberately reintroduced defect: `localhost` in the
sample and in the guide, a wrong port in each, the runtime's port as a client's base URL in the guide
and in the README, `@ai-sdk/openai`, `small_model` removed, an agent's model
pointed elsewhere, an `apiKey` and an `env` list added, a misspelled header in either case, `TIMEOUT`
for `TIMEOUTS`, a single underscore between segments, a misspelled capitalised argument, and a
misspelled error code in two guides. The guide's commands were run as written on one
Windows workstation against LM Studio 0.4.20 with a small model, as was OpenCode 1.18.7 with the sample;
Cline was not run, and its table was checked against the 4.1.22 extension instead. That checks the
instructions and is not a compatibility result.

### Stage 1D, part 5: tagged releases as reviewed drafts

The Windows package exists to be installed without the SDK, so it has to be downloadable, and a CI
artifact needs a GitHub sign-in and expires. A version tag now becomes a draft pre-release carrying the
package and its digest, which a maintainer reviews and publishes (ADR 0018).

- New `.github/workflows/release.yml`, on `v*` tags only. It has three jobs:
  - `version` checks that the tag names the version the build stamps and that a `LICENSE` file
    exists;
  - `ci` runs the whole of `ci.yml`, now callable through `workflow_call`, against the tagged commit;
  - `publish` downloads the `agentsplice-win-x64` artifact that run built and smoke-tested, verifies
    it with `sha256sum --check --strict`, and runs
    `gh release create --draft --prerelease --verify-tag`.
- Only `publish` holds `contents: write`, and it checks nothing out and installs nothing, so no
  dependency's install script runs with the token in reach. Everything else, and the workflow's
  default, is read-only.
- Nothing is rebuilt for the release: what is published is what CI tested. The image is not pushed to
  a registry; the notes say to build it from the tag.
- The first release cannot be cut until a license is chosen and `LICENSE` added, as
  `LICENSE-CHOICE.md` asks.
- `docs/DEVELOPMENT.md` gains a "Releasing" section. `docs/SECURITY.md` "Dependency and release
  security" now states what exists — CI-built drafts, one writing job, digests, pinned inputs — and what
  does not: dependency automation, SBOM, scanning, signing, attestation. `docs/THREAT_MODEL.md` adds the
  release pipeline as a trust boundary and a "Release artifact integrity" entry. The roadmap marks the
  release workflow done.

Every new guard was verified to fail against a deliberately reintroduced defect: a release without
`--draft`, write access granted to the whole workflow, a checkout in the publishing job, and a renamed
artifact. The workflow itself runs only on a tag, so its first real run is the first release, and that
run's output is a draft that can be deleted.

### Stage 1D, part 4: a Windows package that needs no .NET SDK

On Windows without Docker the only route was building from source, and a published executable would
not have run from anywhere but its own folder: it read its settings from the working directory and,
even with those found, created its store wherever it was started. There is now a self-contained
package, built and smoke-tested on Windows in CI, and one guide for every way to install (ADR 0017,
SPECIFICATION section 22 criterion 13).

- `src/AgentSplice.Api/Properties/PublishProfiles/win-x64.pubxml` publishes a self-contained,
  single-file win-x64 gateway. It is not trimmed, compressed, or ReadyToRun; `e_sqlite3.dll` and
  `appsettings.json` sit beside the executable; and it has no IIS assets and no development settings.
  Nothing package-specific is in the project file, so the container publish and the test projects are
  unchanged.
- The profile sets a runtime switch, read by the new `ContentRootDefault`, that makes the executable's
  directory the content root. The package therefore finds its settings and dashboard wherever it is
  started from. An explicit `--contentRoot`, `ASPNETCORE_CONTENTROOT`, or `DOTNET_CONTENTROOT` still
  wins, and every other build gets exactly `CreateBuilder(args)`.
- **Behaviour change:** a relative SQLite `Data Source` now resolves against the content root, as
  `appsettings.json` and the dashboard's `rootPath` do, rather than against the working directory.
  Nothing changes under `dotnet run` or in the image. Absolute paths, `:memory:`, `Mode=Memory`, `file:`
  URIs, and `|DataDirectory|` are used as written.
- New event `1305 MetadataStoreOpened` names the store file once it is migrated: the data source,
  never the connection string. A store whose directory is missing or read-only still fails startup,
  now with an error naming the file; the gateway still creates no directory.
- `.github/scripts/package-windows.ps1` builds the dashboard, publishes with the profile, copies the
  dashboard to where the shipped `rootPath` looks, and writes `agentsplice-<version>-win-x64.zip` and
  `SHA256SUMS`. `README.txt` in the package names the .NET runtime it carries, which nothing services
  afterwards.
- CI's new `package-windows` job on `windows-latest` runs that script and
  `.github/scripts/smoke-windows-package.ps1`, then uploads the `agentsplice-win-x64` artifact. The
  smoke script verifies the digest and the package's contents, then starts the package twice from an
  empty folder elsewhere:
  - with the defaults, it checks the dashboard, `/api/v1` without a token, a refused request read back
    from the store, the store beside the executable, nothing written to the working directory, and a
    listener on `127.0.0.1` only;
  - with a token, it runs the shared smoke test.
- `smoke-image.sh` is now `smoke-gateway.sh`, and it gains backslash traversal vectors, as does the
  in-process traversal test. The image job also checks that 1305 names `/data/agentsplice.db`.
- New `docs/INSTALLATION.md` covers Docker, the Windows package, and running from source:
  - verifying the digest and removing the downloaded-file mark;
  - extracting under the user's own profile, and the SmartScreen and Smart App Control prompts;
  - setting a token, configuring through arguments and environment variables, and moving the store;
  - backup, upgrade, uninstall, and troubleshooting.
- `docs/SECURITY.md` gains a "Local installation" section. `docs/THREAT_MODEL.md` gains "Evidence at
  rest" and "Unserviced bundled runtime" and extends the install-folder and DNS-rebinding entries to
  the package. The specification, architecture, development, observability, testing, and roadmap
  documents describe the package; the roadmap marks direct-process setup and loopback-only defaults
  done.

Every new guard was verified to fail against a deliberately reintroduced defect: a raw `UseSqlite`, a
content-root override that ignores an explicit one, a switch name that drifts from the gateway's
constant, and a package published without the switch, which the smoke test catches when the package
exits before answering. On a development workstation the package was 43.4 MB, and both smoke runs
passed against it.

### Stage 1D, part 3: the image reports its health

The image had no `HEALTHCHECK`, because its base ships no HTTP client to run one with, so Docker could
say only that the container was running and nothing could wait for the gateway to be up. The image now
carries its own probe and reports health from it (ADR 0016, FR-HEALTH-001, FR-HEALTH-002).

- New `src/AgentSplice.HealthProbe`: a console application that references only the base class
  library. It sends one `GET` to the loopback liveness URL it is given and exits `0` on any 2xx and `1`
  otherwise — a non-2xx status, a refused connection, or no answer within three seconds. It never exits
  `2`, which Docker reserves. It refuses any target that is not an `http` URL on a loopback IP literal,
  `localhost` included, without sending anything; uses no proxy; follows no redirect; never reads the
  body; and writes one line per run, which Docker keeps in `docker inspect`.
- The Dockerfile publishes it to `/app/probe/` with workstation GC, apart from the gateway, and declares
  `HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --start-interval=2s --retries=3` in exec
  form against `http://127.0.0.1:5280/health/live`. Liveness, never readiness, so LM Studio being down
  never marks the gateway unhealthy. `docker compose up --wait` now returns once the gateway is healthy,
  and `docker compose ps` shows it. Outside Swarm Docker restarts nothing on an unhealthy container, and
  AgentSplice adds no policy that would.
- The Compose file declares no health check of its own, and says that one must be added to match if
  the container port ever moves off 5280.
- CI's `image` job starts the stack with `docker compose up --build --wait` instead of a `curl` loop on
  the runner, then checks the reported status and that the probe exits exactly `1` against a dead port
  and against a target it must refuse.
- The README and the development guide use `--wait`, and the README gains a PowerShell equivalent of
  its token setup.
- `docs/API.md`, `docs/OBSERVABILITY.md`, `docs/ARCHITECTURE.md`, `docs/SPECIFICATION.md` §20.2,
  `docs/TESTING.md`, and `docs/THREAT_MODEL.md` (a new "Container health probe" entry) describe the check.
  The roadmap splits "Docker image, published" into the image with a health check, done, and publishing
  it to a registry, still open, and marks the Compose example using `host.docker.internal` done, which
  it has been since Stage 0.

Every new guard was verified to fail against a deliberately reintroduced defect: a `curl` health check,
a `localhost` target, port 8080, `/health/ready`, a two-second Docker timeout, the probe copied
somewhere the check does not look, a Compose override, a probe that demands exactly `200`, one that asks
with `HEAD`, and one that accepts `localhost`.

### Stage 1D, part 2: the gateway serves the dashboard

The dashboard ran only under `pnpm dev`, so an alpha installation had no way to open it. The gateway
now serves the build itself, at `/dashboard/` on its own origin, and the container image builds it in
(ADR 0015, FR-DASH-007, FR-DASH-008).

- `docker compose up --build` serves the dashboard at `http://127.0.0.1:5280/dashboard/`, and `/`
  redirects there while it is served. The Dockerfile gains a `node:22-bookworm-slim` stage with a
  pinned `pnpm` and a frozen lockfile; nothing about installing the image needs Node.
- Confined to one middleware branch under `/dashboard`. Every client route there is answered with
  `index.html`, dots included; a missing file under `/assets/` is a 404, never the page; methods other
  than `GET` and `HEAD` are a 405. No route outside `/dashboard` answers differently, which a test
  proves by comparing every API answer with a gateway serving no dashboard — and which a root
  `MapFallbackToFile` fails, turning `GET /v1/chat/completions` from 405 into a 200 page.
- Every dashboard response carries a content security policy admitting only same-origin script,
  style, and connections, with no inline script and no framing, plus `nosniff`, `X-Frame-Options:
  DENY`, and `Referrer-Policy: no-referrer`. Checked against the built bundle in a real browser on all
  four screens, the waterfall's bars included: no violations. The CSP the threat model listed as a
  mitigation for dashboard XSS now exists.
- Hashed files under `/dashboard/assets/` are cached as immutable; the page is revalidated on every
  load and answers a conditional request with 304.
- The page needs no credential and holds no evidence; `/api/v1` still requires the token from every
  caller when one is configured.
- Optional, as FR-DASH-002 requires. `agentsplice:dashboard:enabled` (default `true`) and
  `agentsplice:dashboard:rootPath` (default `dashboard`, relative to the content root, so
  `/app/dashboard` in the image). A directory counts as a build only with `index.html` and an
  `assets` directory. With no build the gateway starts, serves every API, and logs
  `1204 DashboardBuildMissing` once with the path it checked; serving logs `1203 DashboardServed`.
  The .NET build never invokes Node.
- The dashboard is built with `base: '/dashboard/'` and an explicit `assetsDir`, the router takes its
  basename from Vite, and the dev server moves to `http://127.0.0.1:5281/dashboard/`. Tests on the
  gateway's side fail if the mount and the build disagree.
- `.dockerignore` anchors `node_modules`, `dist`, and the SQLite files at every depth. Without the
  leading `**/` they matched only at the context root, so a host's `node_modules` — native binaries
  for the host — would have overwritten the image's own install, and a development `agentsplice.db`
  holding trace metadata entered the build context.
- CI gains an `image` job: it builds the image through Compose, as an alpha user would, and runs
  `.github/scripts/smoke-image.sh` against it — the redirect, the page and its headers, every asset the
  page names, a deep link, traversal attempts through real Kestrel normalisation, the unchanged API
  answers — and checks the startup log for 1203 and no 1204. Nothing built the image in CI before.
- The Dockerfile's stale note that the health endpoints did not exist yet is corrected.

Every new guard was verified to fail against a deliberately reintroduced defect: a root fallback, a
Dockerfile copying the build where the settings do not look, and a root-anchored `.dockerignore`
pattern.

### Stage 1D, part 1: the OpenTelemetry SDK and OTLP export

The SDK deferred since Stage 1A arrives with the exporter that justifies it (ADR 0014, FR-OBS-001,
FR-OBS-002). Spans and metrics can now leave the process; by default they still do not.

- The OpenTelemetry SDK replaces the hand-registered `ActivityListener` as the only subscriber to the
  `agentsplice.*` sources, and runs whether or not anything is exported, because
  `x-agentsplice-trace-id` needs the span either way. Every exchange is still sampled:
  `AlwaysOnSampler`, not the SDK's parent-based default, so a client's unsampled `traceparent` cannot
  discard spans for its own exchange.
- `agentsplice:telemetry:otlp` turns on OTLP export of traces and metrics, over `HttpProtobuf`
  (default) or `Grpc`. It is off by default and the destination is never defaulted; enabling it
  without an endpoint fails startup. The endpoint is validated even while export is off, and one that
  embeds credentials, carries a query or fragment, or names `/v1/traces` where the receiver's base
  address belongs is refused.
- `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_PROTOCOL` have no effect. A variable left over
  from another tool must not decide where a description of every exchange goes; an integration test
  points both at a live receiver and asserts nothing arrives. `OTEL_EXPORTER_OTLP_HEADERS` still
  works, and is how a collector credential stays out of AgentSplice's configuration.
- Proven against a real receiver over both transports — gRPC over cleartext HTTP/2 included — and
  against every sentinel the log-redaction suite uses plus the model identifier: no prompt, output,
  tool argument, model, client request identifier, or credential appears in any exported byte.
- Export never affects an exchange: an unreachable collector costs a failed export on a background
  thread. The cost is that such a failure is reported only through the SDK's own event source; the
  gateway announces that export started (event `1202 TelemetryExportEnabled`) but not that it is
  failing.
- `/api/v1/system` reports `telemetryExportEnabled` beside the two retention flags, and the dashboard's
  Overview shows it. The destination is not reported.
- An optional `otel-collector` Compose service (`--profile otel`, `otel/opentelemetry-collector:0.162.0`)
  receives both transports on the Compose network, publishes nothing on the host, and prints what it
  receives. `.env.example` loses the `OTEL_EXPORTER_OTLP_ENDPOINT` line nothing had ever read.
- Logs are not exported and no framework instrumentation is enabled; tests fail if an OpenTelemetry
  logger provider appears or if anything outside `AgentSplice.Observability` references the SDK.
- Known limitation: the exchange span's parent is ASP.NET Core's request activity, which is not
  exported, so backends show the exchange span with a missing parent. The trace identifier is
  unaffected.

Every new guard was verified to fail against a deliberately reintroduced defect: a model identifier
written onto a span, export that ignored the `enabled` switch, and the hand-registered listener
restored beside the SDK.

### Stage 1C, part 6: the minimal dashboard

Stage 1C's last piece, and the first consumer of the API the previous parts published. `web/AgentSplice.Dashboard`
is React, TypeScript, and Vite, gated in CI by its own `pnpm lint`, `pnpm test`, and `pnpm build` job.

- Overview, Exchanges, Exchange Detail with a latency waterfall, and Runtimes (FR-DASH-003,
  FR-DASH-004). It is a client of the documented `/api/v1` endpoints and holds no database
  connection, no configuration file, and no knowledge of the store's schema (FR-DASH-002).
- A phase that was never observed is listed and marked unknown rather than drawn as a zero-width bar
  or dropped. A missing bar reads as "took no time" and a missing row as "does not apply", and both
  are wrong for a phase that simply was not measured (FR-DASH-006).
- Every measurement is shown with its provenance, and the weaker ones are labelled where the value
  appears rather than only in a table nobody scrolls to (FR-OBS-010).
- Prompt processing and generation are never one bar, and no figure is offered as prompt throughput.
  The note under the waterfall explains the absence instead of filling it (FR-OBS-005).
- No content is rendered anywhere, and the retention notice says so rather than leaving a reader to
  notice the absence and suspect a bug (FR-DASH-005).
- The timeline is ordered by recorded sequence, not by timestamp. A host clock that stepped backwards
  mid-exchange orders two timestamps impossibly; the gateway keeps that visible on purpose, and
  re-sorting would hide the anomaly it preserved.
- The administrative token is held in memory only. In `localStorage` it would survive the tab, be
  readable by anything injected into the origin, and outlive the operator's intent.
- Writing the tests caught a defect: `toLocaleString()` follows the machine's locale, so on a pt-BR
  host 1024 tokens rendered as `1.024` — a number an English-reading operator parses as one. Numeric
  evidence is now formatted in a pinned locale, because evidence has to mean the same thing to
  whoever opens it.

### Stage 1C review corrections

Directed review of the finished backend found three material problems, one of them a regression the
slice introduced into the repository's own container configuration.

- **Docker and Compose could not start.** `AdministrationBindingGuard` correctly treats the
  container's `0.0.0.0:5280` as network-reachable and refuses without a credential, and nothing
  shipped one. Compose now requires `AGENTSPLICE_ADMIN_API_KEY` with no default, failing at
  `docker compose config` with a message naming the variable — a default token is a token everybody
  knows. The guard was not weakened: a process inside a container cannot tell that the host published
  its port on loopback only.
- Two Dockerfile defects surfaced while proving this and had been latent since the projects existed:
  the restore stage copied four of seven manifests, so `--no-restore` publish failed with
  `NETSDK1004`, and `.editorconfig` never entered the build context, so the analyser severities this
  repository waives — `CA1848` among them — were errors inside the image.
- **Readiness consulted runtimes with the option off.** Health was evaluated before
  `requireReachableRuntime` was read, so a probe could wait out a connect timeout on a deployment
  whose configuration had explicitly said reachability was not a readiness condition — long enough for
  an orchestrator's own probe to expire and take a healthy gateway out of rotation. The early return
  is now the contract, and `reachableRuntimes` is absent rather than zero when nothing was consulted.
- **A local reverse proxy could bypass the administrative token.** Trusting a loopback remote address
  is wrong behind nginx or Caddy, which connect to Kestrel from `127.0.0.1`, so every relayed request
  looked local. Reading `X-Forwarded-For` without trusted-proxy configuration would be worse — that
  header is caller-supplied. A configured token is now required from every caller, loopback included,
  which makes a relayed request and a local one satisfy the same check.
- `AdministrationBindingGuard` now runs against `app.Configuration` after `Build()`. Reading
  `builder.Configuration` inside the composition root is the same defect as ADR 0013 decision 13 one
  layer up: a late binding was invisible to the check meant to catch it.
- The filter returns its refusal as an `IResult` instead of writing the response and returning null,
  which left the framework materialising that null onto a response that had already started.
- `401` now carries `WWW-Authenticate: Bearer`. The envelope still names the mechanism and never the
  presented token, the configured variable, or the caller.
- The OpenAPI draft gained a bearer security scheme, per-operation `400`/`401`/`404`/`500`/`503`
  responses, and a corrected description: timeline and observations return the same ordered sequence
  in Stage 1, which the document had described as a projection that has never existed.
- `.env.example` no longer offers PostgreSQL or content capture as usable; both are refused at
  startup.
- Corrected "constant-time" to what it is: `FixedTimeEquals` returns immediately on a length mismatch,
  so the configured token's length is observable. Acceptable for a random token, and stated.

### Stage 1C, part 5: administrative authentication

- Every `/api/v1` route now requires authorization, applied to the route group so a route added later
  cannot be added unprotected by forgetting. A loopback caller is allowed — that is the deployment
  AgentSplice is built for, and requiring a token there would make reading your own traces need secret
  management first. Anything else needs `Authorization: Bearer <token>`, compared in constant time
  against a value read from the environment variable named by the settings; the setting holds a
  variable name and never a secret.
- Startup refuses a network binding with no token rather than warning about it. A warning in a startup
  log is a message read once, if the level happens to be right, on the deployment least likely to be
  watched. A bare `HTTP_PORTS`, a wildcard host, and `0.0.0.0` all count as network bindings — that is
  what a container publishing a port produces, which is exactly the case worth catching.
- `/health/live` and `/health/ready` stay outside the group. A liveness probe that failed closed on a
  misconfigured token would restart a healthy process.

### Stage 1C, part 4: system, runtimes, model catalogue, and health

- `GET /api/v1/system`, `/runtimes`, `/models`, `/health/runtimes`, plus `/health/live` and
  `/health/ready`.
- Health is derived from the same discovery the request path uses rather than probed separately. A
  second prober would double the load on every runtime and could disagree with what routing sees, and
  a health page saying a runtime is fine while completions to it fail is a page an operator believes.
  A runtime nothing has consulted reports `unknown` with no `checkedAt`.
- That rule caught a defect in the catalogue: `reachable` was a required boolean, and a runtime with
  discovery disabled reported `true` having never been asked. It is nullable now — `false` would read
  as unreachable for a runtime that is fully usable through its aliases, and `true` was an assertion
  nothing supported.
- `/api/v1/models` reports `created` as absent where `/v1/models` emits `0`. The zero is a
  substitution the OpenAI schema forces and belongs to that envelope alone.
- Readiness requires a reachable runtime only when asked to, and the default is off: a gateway whose
  runtime is down is still correctly configured and is still the component able to report the outage.

### Stage 1C, part 3: the exchange, timeline, and observation APIs

The evidence became readable. Parts 1 and 2 moved it from "discarded" to "stored and reachable only
by opening the database file"; this is the surface `openapi/agentsplice-openapi.yaml` and
`docs/API.md` have specified since Stage 0.

- `GET /api/v1/exchanges`, `/exchanges/{id}`, `/exchanges/{id}/timeline`, and
  `/exchanges/{id}/observations`. The handlers read query values, call the application, and write what
  comes back; validation, pagination, status selection, and payload shape are decided in
  `ExchangeQueryService`, which returns the same `GatewayResponse` the completion endpoints use.
- Paged by an opaque cursor carrying the whole sort key rather than an offset. An offset into
  `(startedAt DESC, exchangeId DESC)` skips or repeats rows whenever an exchange is written or expired
  between two pages, which on a gateway still serving traffic is every page.
- Filters are validated, not ignored. A `status` or `runtimeId` outside the published vocabulary is
  refused with `agentsplice_invalid_query` naming the parameter — never echoing its value — because a
  silently dropped filter returns a page that looks like an answer to the question asked and is an
  answer to a different one.
- An identifier that does not parse and one that is not retained answer the same 404. They are
  indistinguishable to a caller who cannot see the store, and separating them would tell anyone
  probing the surface which of their guesses were well-formed.
- A deployment with `persistence:mode: None` answers `503 agentsplice_persistence_disabled` rather
  than an empty page. Ephemeral operation is supported, and on such a deployment "no exchanges are
  stored" and "no exchanges happened" are both true while only one answers the question.
- Administrative error codes are a separate published set from the core ones. The core set is the
  completion path's vocabulary and is deliberately the same size as `FailureClass`; reading stored
  evidence is not an exchange and has no failure class, so folding them together would have meant
  inventing one for "the caller asked for a row that is not there".
- The stored structural summaries are served through unchanged, embedded rather than reparsed. The
  schema declares them `additionalProperties: true` precisely so a summary can gain a field without a
  contract change, and a serialiser would have made the writer the thing that has to keep up.
- `ExchangeSummary.streaming` became nullable in the OpenAPI draft. A request refused before its
  envelope was read never stated a preference, and `false` would be a claim about a body nothing
  parsed.
- Normalised the working tree back to LF. Editing scripts had rewritten eleven files as CRLF, which
  `.gitattributes` forbids and which broke a contract test that reads the document it verifies.

### Stage 1C, part 2: retention sweep and the settings this build cannot honour

- `RetentionSweepService` removes exchanges past `capture:retention:metadata` in bounded batches,
  sweeping once at startup and then on `capture:retention:sweepInterval`. A gateway restarted more
  often than its interval would otherwise never sweep at all. Idempotent by construction, and
  auditable by counts and the window rather than by naming what was deleted — a log that names deleted
  evidence is a copy of it that outlives the policy.
- `contentEnabled` and `PersistenceMode.Postgres` are now refused. The first claimed prompts were
  retained under a sanitiser that does not exist; the second was silently served by SQLite.

### Stage 1C, part 1: SQLite metadata store and the write path

Exchanges survive the process. Until now every exchange produced a complete timeline, measurements
with provenance, and a structural summary, and then handed all of it to a sink that discarded it —
a gateway whose product is evidence, retaining none. Recorded in
[ADR 0013](docs/adr/0013-stage-1c-metadata-store.md), which supersedes decision 12 of ADR 0009 and
discharges the Stage 1C consequence ADR 0008 recorded.

- Added `AgentSplice.Infrastructure.Persistence`: an EF Core SQLite store with three tables, a bounded
  in-process queue, and a background writer. EF Core is referenced by `AgentSplice.Infrastructure`
  alone, which an architecture test now enforces.
- Rows are separate types from the domain records. `CompletionExchange` is immutable with private
  `init` setters and value-object identifiers, so mapping it directly would put persistence
  conventions inside `AgentSplice.Domain`. The separation also closes the gap ADR 0008 left open: a
  request refused before its envelope was read has no `CompletionExchange`, and the store can record
  it with a null model and a null streaming preference rather than not recording it at all.
- The model is provider-neutral — no SQLite type names, no raw SQL, timestamps as UTC ticks — because
  FR-DATA-003 commits to PostgreSQL through the same contracts and a model that must be rewritten to
  honour that is not a contract.
- `RecordAsync` never waits. It calls `TryWrite` and returns, so a slow store cannot become gateway
  latency; the write itself runs on a background service in short transactions.
- The persistence boundaries are stamped by whatever produced them. `MetadataQueued` comes from the
  sink as the record enters the queue, `PersistenceCompleted` from the writer after the commit
  returned — a second small transaction, because a boundary named "completed" that was stamped inside
  the transaction it describes is the defect class ADR 0010 exists to prevent. `PersistenceFailed`
  deliberately has no row: the store that rejected the write is the one it would have to live in, so
  it is a log event, a counter, and nothing pretending to be evidence.
- Three defects were found by their own tests before they could ship. `BoundedChannelFullMode.DropWrite`
  reads like the intent and is not — `TryWrite` returns `true` having discarded the record, so every
  drop was silent and the counter never moved; the channel now uses `Wait`, under which `TryWrite`
  refuses and reports. Reading `IConfiguration` at registration time to decide whether to register the
  store read it half-built, silently disagreeing with the `IOptions` value the rest of the system
  uses; the decision now happens when a service is resolved. And passing the shutdown token to
  `SaveChangesAsync` discarded, on every host stop, records that had already left the queue — the wait
  for more work is cancellable, the write it leads to is not.
- Added `agentsplice.persistence.failures` with a two-value `failure_reason` dimension, and made
  `agentsplice.persistence` a live span source. Saturation and a rejected write are different problems
  with different fixes, and one undifferentiated count would send an operator adding queue capacity to
  a database refusing every write.
- Closed three channels through which caller-chosen text reached the store with content capture
  disabled. A structural summary was bounded in length and count, and that had been treated as making
  it safe; it is not. A client picks the value of `role` and the name of every JSON property it sends,
  and a runtime picks the value of `finish_reason`, so truncating those bounded how much caller text
  was retained and nothing else. Roles and finish reasons are now matched against a closed vocabulary
  and bucketed when they do not match; unknown field names are hashed, so an operator can still ask
  whether a field was forwarded without the name itself being stored. The existing privacy test missed
  all three because it put its sentinels in `content`, which the gateway never reads — the new one
  puts them in every field AgentSplice actually records, and fails against a reintroduced copy of the
  defect.
- Removed a fault channel found in the same place: the field-name helper *threw* on a control
  character, so a hostile `role` turned client input into a failed request.
- Made vocabulary matching exact and ordinal. The first version of the fix trimmed and lower-cased
  before comparing, so `" User "` was stored as `user` — a token the client never sent. Closing a
  leak by persisting a tidied copy of the protocol and serving it as the observation is not a fix;
  a non-canonical role is a fact, and `(unrecognised)` is what reports it.
- Stopped a client forging an AgentSplice observation. The buckets are outputs and were being
  accepted back as inputs, so `{"role": "(unspecified)"}` was recorded as a message that stated no
  role. Absence now travels out of band, as a separate count the scanner supplies, because a
  dictionary key is a string and every string is forgeable while the absence of one is not.
- Stopped dropping blank finish reasons, which made `"finish_reason": ""` indistinguishable from a
  response that carried none.
- Stopped logging the client's own correlation token. `x-request-id` is accepted as up to 128
  characters of printable ASCII, which prevents header injection and nothing else — a client is free
  to put a name or a ticket subject in it. Four AgentSplice log sites wrote it, and a fifth path
  wrote it with no AgentSplice involvement at all: `IHttpClientFactory` logs request headers at
  `Trace` and the token is forwarded upstream. Logs now carry `ExchangeId`, and the header joins the
  HTTP-client redaction list. `IdentifierText`'s claim that its validation kept content out of
  observability was false and is corrected.
- Documented `ClientModelId`, `PublicRequestId`, and `UpstreamRequestId` as operational metadata
  retained verbatim: chosen by untrusted parties, potentially sensitive, kept whole because diagnosis
  needs them, and authorized like any other stored evidence.
- Stopped every persisted row claiming that nothing was retained. `ExchangeRecorder.Accept` opens each
  exchange as `Disabled` and cannot know better, so the row carried that through while holding
  summaries, observations, and measurements. The store stamps `MetadataOnly`, because the store is
  what retained — including for records that have no `CompletionExchange` to carry the state at all.
- Stopped a backwards clock producing a `persistence.duration` of zero with `Measured` provenance and
  an end preceding its own start. An impossibly ordered interval now yields no measurement, as it
  already did in the recorder's duration builder and the gateway's histogram guard; the boundaries
  stay, so the anomaly is still diagnosable.
- Made `MeasurementNames.PersistenceDuration` producible. It had been declared since Stage 1A with
  nothing able to emit it. The store now writes it queue-to-durable per exchange, rather than as the
  batch's write time — a batch covers many exchanges, and attributing its duration to each would
  report one number N times and overstate every one.
- Added the persistence-failure fixture family `docs/TESTING.md` recorded as still owed by Stage 1B.
  It asserts the policy rather than a broken file: a refusing context factory, and the failure logged
  with a stable event ID, counted per exchange lost rather than per batch, dropped rather than
  retried, with the writer still draining and nothing reaching the caller.
- Restored the coverage the test-host change removed. Integration hosts now force persistence off so
  that tests do not each create a database, which left nothing exercising the shipped persistence
  block; a contract test asserts it against `appsettings.json` directly, including that the mode named
  there has a provider in this build.
- Added an architecture test that the API never reaches for a `DbContext`. It composes persistence and
  must not query it, and the module-boundary test that confines EF Core to Infrastructure does not
  cover the API assembly.
- Quieted EF Core's command logging to `Warning` in the shipped settings, with a contract test.
  Parameters are redacted unless sensitive-data logging is enabled, so this is noise rather than
  leakage — but a store that narrates itself into the default log is one setting away from being the
  other thing.
- Removed `UnimplementedPersistenceNotice`. It existed to say the store had not shipped.
- Deferred the OpenTelemetry SDK swap from Stage 1C to Stage 1D, in `docs/OBSERVABILITY.md` and the
  architecture test that referenced it. Until an exporter exists, adopting the SDK adds a dependency
  and changes nothing an operator can see.

### Stage 1A/1B correctness review, third pass

One defect, introduced by the previous pass. Recorded in
[ADR 0012](docs/adr/0012-classification-independent-of-relayability.md), which refines ADR 0011.

- Stopped protocol classification depending on whether the content type could be relayed. Splitting
  the header into an evidence token and a relayable value was right; asking the relayable one whether
  the body is an event stream was not. `RelayableContentType` is null when the header is too long to
  write back, so a conforming `text/event-stream` carrying 1100 characters of parameters was
  classified as buffered — the client got `text/event-stream` from the fallback and read a stream,
  while the gateway ran no SSE framing, ignored `[DONE]` and waited for EOF or an idle timeout,
  recorded no decoded or semantic boundaries, and wrote `upstream.streamed = false` into the evidence.
  Metadata now carries a third value, `ParsedMediaType`, parsed from the header as received before
  any bound has discarded anything, and classification uses only that. Relayability answers whether
  the whole header may be written to the client; it never answered what the body is.
- Moved the RFC 9110 media-type grammar into `AgentSplice.Domain` so the parse can happen at the
  moment the header arrives. `OpenAiMediaTypes.IsEventStream` is now one comparison against the
  parsed media type, which keeps one implementation of the grammar rather than two.

### Stage 1A/1B correctness review, second pass

Four more defects, found by reviewing the previous pass. Two of them are the same shape as the first
round one layer down: a bound enforced in the ordinary case and skipped in the interesting one, and a
claim in the documentation that the code did not quite honour. Recorded in
[ADR 0011](docs/adr/0011-per-event-bounds-and-media-type-parsing.md), which refines ADR 0010.

- Bounded a **completed** streamed event, not only one still being assembled. The frame reader checked
  the bytes after the last complete frame, and completing a frame resets that count — so an event that
  crossed `maxStreamEventBytes` in the same append that carried its terminating blank line was
  accepted in full. The ceiling held for every event except the ones that reached it. Each frame is
  now measured as it completes, and an oversized one is neither handed out nor stepped over.
- Stopped a bound violation from retracting a completion the client already holds. The relay enforced
  the bound before draining, so a `[DONE]` that completed earlier in the same read was discarded
  unread and the exchange was recorded as `InvalidUpstreamStream` — for a stream that had terminated
  correctly, whose terminator bytes the client had already received. Draining now precedes
  enforcement, and the terminator wins. A violation *ahead* of the terminator still ends the stream:
  there is no completion to protect, and a client must not be handed a truncated stream that closes
  as though it were whole.
- Parsed the content-type parameters instead of skipping them. The previous fix took everything before
  the first semicolon and compared it, which accepted `text/event-stream; ===` — the mirror image of
  the whole-string equality it replaced. Classification now validates the full RFC 9110 grammar, with
  no `System.Net.Http` dependency, since an architecture test keeps transport types out of the
  protocol modules. `text/event-stream;` stays valid: the grammar writes the parameter itself as
  optional, and refusing it would reject a legal sender.
- Made the relayed `Content-Type` actually verbatim. It was still passing through the evidence
  sanitiser, which truncates at 256 characters — and a header cut there ends inside a quoted parameter
  or halves a multipart `boundary`, producing a header the runtime never sent. The relayed value is
  now validated rather than repaired: over 1024 characters or carrying a control character it is
  refused outright and the normalised media type is sent instead, which says less than the runtime did
  rather than something untrue. The two values are bounded for their own reasons and by their own
  rules.
- Renamed `SseFrame.IsCommentOnly` to `DispatchesClientEvent`. The logic was right — the SSE grammar
  dispatches nothing when the data buffer is empty — but the name covered a bare `id`, a `retry`
  directive, and an `event` name with no payload as well, and invited the delivered-event count to be
  read as excluding keepalives alone.

### Stage 1A/1B correctness review

Five defects found by reviewing the finished Stage 1A and Stage 1B slices. None changed what a client
received; every one changed what AgentSplice said it had observed, which for a diagnostic tool is the
more serious of the two. Two existing tests had to be replaced rather than extended, because they
asserted the defective behaviour as the contract. Recorded in
[ADR 0010](docs/adr/0010-correct-stream-boundary-and-termination-semantics.md), which supersedes
parts of ADR 0009.

- Stamped each streaming boundary at the operation that produced it. The relay took one clock reading
  *before* awaiting the upstream read and reused it for the first upstream byte, the first decoded
  event, the first semantic event, and the first client flush. Four distinct boundaries collapsed
  onto one instant, and that instant preceded all four: a runtime that thought for twenty seconds had
  its first byte dated twenty seconds early, and every interval between the four was exactly zero —
  indistinguishable from a gateway that is infinitely fast and from one that is not measuring at all.
- Separated the first client event from the first decoded frame. A comment or keepalive may set the
  decoded boundary and never the client-event boundary, since a conforming client raises no event for
  it and dating first delivery from a keepalive reports a response as having reached the client
  before it carried anything.
- Made the timeline append boundaries in the order they occurred rather than the order they were
  learned. The relay writes before it decodes, so the flush timestamp is earlier than the decode that
  revealed it; appending as learned left the timeline running backwards whenever a keepalive and a
  data event arrived in one read. A negative interval is dropped rather than reported, so the symptom
  was a whole latency phase disappearing.
- Stamped the buffered path's first upstream byte inside the body reader's callback. It was read
  after the whole body had been buffered, so the boundary really named "the body finished" — or, on
  the failure branches, "the body failed". For a long generation the two are the entire length of the
  response apart, which is exactly when the boundary is worth having.
- Moved stream media-type classification into the protocol, matching RFC 9110 rather than comparing
  the whole header for equality: `text/event-stream; charset=utf-8` is an event stream. The relay and
  the orchestrator now ask the same implementation the same question instead of repeating a literal.
- Stopped rewriting the runtime's `Content-Type` on its way to the client. The normalised token kept
  for evidence was being forwarded, which silently dropped a `charset` a client decodes by and would
  have discarded a `boundary` a body cannot be parsed without. The header now reaches the client
  verbatim; the bounded token stays where the bounded token belongs.
- Inspected `FlushResult` instead of discarding it. `PipeWriter` reports a completed or cancelled
  pipe without throwing, so a client that stopped consuming could go unnoticed: the relay kept
  reading the runtime and kept counting bytes as delivered to someone who was gone.
- Made the first valid `[DONE]` the end of the response. The relay used to read on to EOF or an idle
  timeout, which ADR 0009 accepted as costing latency but not accuracy. It cost both — completion was
  dated from whatever ended the transport, so a runtime that lingered after finishing stretched the
  upstream duration and the generation window derived from it across a stall that produced nothing.
  No read is issued after the terminator, and a repeat terminator is neither read nor forwarded: a
  second `[DONE]` is not a second completion. The price is one connection per lingering runtime,
  which is the right trade.
- Corrected the public status in `README.md` and `src/README.md`, which still described Stage 0 as
  current and the host as exposing no HTTP endpoints.

### Stage 1B — Streaming correctness and timeline

- Added streaming completions (FR-STR-001 to FR-STR-012). A `stream: true` request is forwarded with
  `Accept: text/event-stream` and the runtime's answer is relayed byte for byte as it arrives.
- Relayed raw bytes rather than decoded events. Each chunk is written and flushed to the client
  before anything decodes it, so no evidence-gathering work can become a flush delay, and valid SSE
  holds by construction: split events, split UTF-8 sequences, CRLF, multi-line `data`, comments, and
  keepalives all reach the client unchanged because nothing rebuilt them. Decoding and re-emitting
  would have normalised escape forms and number formatting, exactly as re-emitting a request document
  would have.
- Separated framing from meaning. The frame reader knows where an event begins and ends; the protocol
  module knows what `[DONE]` is and which chunk first carried output. SSE is a grammar shared by more
  than one protocol, and fusing the two would make the next protocol a rewrite rather than an
  implementation.
- Made the first-token boundary honest. A first chunk announcing `role: assistant` sets
  `FirstDecodedEvent` and deliberately not `FirstSemanticEvent`, because time to first token is not
  time to first chunk — the same class of error as labelling prompt throughput as generation
  throughput.
- Fixed a live Stage 1A defect: every timeline boundary was stamped when control returned to the
  orchestrator, so `agentsplice.time_to_headers` was measuring time until the whole body had been
  read. Boundaries are now stamped from the moment they were observed. A plausible, published, wrong
  metric is worse than an absent one.
- Distinguished what the client asked for from what it was served. A `stream: true` request answered
  with a buffered JSON body never streamed, and requiring it to record how its stream ended would
  have fabricated evidence. The exchange records `upstream.streamed = false` and is summarised
  exactly as it would have been without the flag, so asking to stream never costs evidence.
- Classified every way a stream can end: normal completion, the protocol terminator, client
  cancellation, timeout with its phase, a malformed event, a lost connection, and a gateway bound.
  `LimitExceeded` is deliberately distinct from `MalformedEvent` — one is the runtime's behaviour and
  the other AgentSplice's own policy, and reporting the second as the first misattributes a gateway
  decision to the runtime.
- Made a malformed payload an observation rather than a failure. It is relayed verbatim and the
  exchange completes with no failure class, because the client's own parser is the authority on the
  runtime's protocol. A bound violation or a lost connection abandons the response instead: once the
  status is committed, a stream that stops early but closes cleanly is indistinguishable from a
  complete one.
- Wired the idle-stream timeout, configured and validated since Stage 0 and unreachable until now.
  The budget is re-armed per read rather than allocated per read, and the response-header budget is
  disarmed once headers arrive — left armed, its token fires during every long stream, and every
  mid-stream stall would have been reported as a runtime slow to answer.
- Added `limits:maxStreamEventBytes` and `limits:maxConcurrentCompletions`, closing the concurrency
  debt `docs/SECURITY.md` recorded. The streaming path retains a read buffer plus one event under
  assembly and nothing else, which makes the memory ceiling a documented product of two settings
  rather than a function of response length. Over the limit a request is refused with `429` rather
  than queued: a queue turns an overload into unbounded latency, which an agent loop cannot act on.
- Added six instruments and a `stream.termination` dimension attached only to exchanges that
  streamed, so buffered series keep their cardinality. Generation throughput is derived over the
  observed decode window and its bias is documented rather than corrected away. Prompt throughput is
  absent by design, not deferred: nothing observable marks the end of prompt processing, so the only
  available interval measures the prompt, the queue, and the network together.
- Gave `agentsplice.stream` a real span and added a contract test that every activity source the
  listener subscribes to has something writing to it. The OpenTelemetry SDK moves to Stage 1C, where
  an exporter has a consumer; three source comments that said 1B were corrected rather than left to
  drift.
- Disabled Kestrel's minimum response data rate. A local model producing one token every few seconds
  falls below the 240 bytes/s default, and Kestrel aborting the response would have been recorded as
  a client disconnect — blaming the client for a limit the gateway imposed on itself.
- Removed `upstream.connect.duration`. No pair of observable boundaries can derive it, and a declared
  name nothing can produce is the defect the previous review slice existed to remove.
- Proved "long streams pass without full buffering" behaviourally: eight megabytes stream successfully
  with the buffered ceiling set to 64 KiB, which can only happen if the streaming path never routes
  through it. `AgentSplice.PerformanceTests` was not created; ADR 0009 records why a wall-clock
  benchmark on shared runners would not have earned a place in CI.
- Added a test-releasable gate to the fake upstream. Chunk delays are real wall-clock waits, so a test
  built on them is either slow or flaky and never both; the streaming, disconnect, and idle-timeout
  suites are now deterministic and run in about a second. Client-side assertions use an independent
  SSE parser, because a test whose parser is the gateway's own proves only self-consistency.
- Added ADR 0009 recording all thirteen Stage 1B decisions, the alternatives rejected, and the two
  stream terminations that remain unreachable and why.
- Measured upstream connection time, which `docs/OBSERVABILITY.md` requires unconditionally and this
  slice had first written off as underivable. It is not: `SocketsHttpHandler.ConnectCallback` exposes
  it, so the provider's handler now opens the socket itself — reproducing the default behaviour
  exactly, since the only reason to take it over is to time it — and stashes the timing on the
  request that triggered the connection, so exactly the request that paid for one is charged for it.
  Recorded as two boundaries and derived like every other phase, and **absent for a request served by
  a pooled connection**: a zero there would claim a connection was opened instantaneously, which is a
  measurement of an event that never happened. Without this phase, a runtime slow to accept
  connections and a runtime slow to think are the same number.
- Cleared pooled buffers that held prompt or model output before returning them. A pooled array
  outlives the exchange that filled it, and content escapes through a later renter that trusts the
  array's length rather than its read count. They are rented once per exchange rather than once per
  read, so this costs a single memset against a stream that ran for seconds.
- Fixed a misclassification CI caught on Windows: a runtime that answered and then died partway
  through its own body was reported as `agentsplice_runtime_unavailable`. The buffered path's catch
  blocks could not tell which phase had failed, so whether a truncated body surfaced as an
  `IOException` or an `HttpRequestException` decided the reported cause — a diagnosis by race. Body
  reads now classify their own failures: once response headers have arrived, "unreachable" is
  factually impossible. The catalogue path had the same defect and the streaming path already
  handled it.
- Made two fixtures deterministic rather than leaving them to that race. A connection reset can
  outrun the bytes that preceded it, so a test pinning one error code to it was asserting a coin
  flip; the strict assertion now uses a runtime that declares a length and stops short of it, and the
  reset case asserts what is true either way. The streamed-reset test gates the reset until the
  client is blocked waiting, because ungated the client could finish reading buffered bytes and see a
  clean end — reporting success for a truncated stream about one run in three.
- Closed three further gaps found by reviewing the slice against the architecture documents.
  `docs/TESTING.md` claimed the duplicate terminal-event family was covered when no test produced
  one. The threat model names excessive nesting among malicious-stream behaviours and nothing
  asserted it, so a five-thousand-deep payload is now proven to be reported as malformed rather than
  overflowing the stack — a failure no catch block could contain. And the error table described a
  `502` for an exceeded stream bound that cannot occur, because the status is committed with the
  response headers before any body byte exists.

Measurements in this slice are taken against the deterministic fake upstream and are fixture
measurements, not hardware claims. Stage 1B makes no compatibility claim; that requires a conformance
report.

### Stage 1A — Review against the architecture documents

A pass over the four Stage 1A slices against `CLAUDE.md`, `docs/ARCHITECTURE.md`,
`docs/SPECIFICATION.md`, `docs/OBSERVABILITY.md`, and `docs/THREAT_MODEL.md`.

- Made the unsupported-field policy explicit, which FR-CHAT-005 requires and Stage 1A only implied.
  `agentsplice:compatibility:unsupportedFields` selects `transparent` (the default, and the reason a
  runtime extension reaches the runtime) or `strict` (reject any top-level field AgentSplice does not
  model, naming it). `adapted` stays undeclared: adapters are Stage 4, and a mode that cannot be
  applied is a policy in name only.
- Added a startup notice when a persistence mode is configured that this build does not implement.
  The shipped settings select SQLite because FR-DATA-002 makes it the local default when persistence
  is enabled, so without a notice an operator reading their own configuration would expect a database
  file and accumulating exchanges and get neither, with nothing to explain it. Running with
  `mode: None` is a supported configuration and stays silent.
- Narrowed the activity-source subscription to the two sources this stage starts spans on.
  `agentsplice.stream` and `agentsplice.persistence` belong to the Stage 1 list the specification
  declares, but nothing writes to them yet, and subscribing to a source that produces nothing is the
  same defect the existing `Later_stage_activity_sources_are_not_declared_yet` test already guards
  against for later stages.

- Removed `upstream_status_error`. It was published in the client contract with nothing able to emit
  it: an upstream non-2xx is relayed verbatim with the runtime's own body, so AgentSplice writes no
  envelope and there was nothing for the type to describe.
- Gave `agentsplice.model_discovery.duration` a producer. It was declared as a live instrument and
  never fired. A failed refresh is timed too, because how long a runtime takes to refuse is as
  diagnostic as how long it takes to answer.
- Withdrew the `x-agentsplice-trace-id` claim from `GET /v1/models`. The OpenAPI declared a header the
  endpoint never sent: model discovery is not an exchange, none of the four declared activity sources
  covers it, so it has no span and must not advertise one.
- Added a test for the class of defect all three share —
  `Every_declared_error_type_has_something_that_produces_it` — because the existing contract tests
  only checked that a name was *documented*, not that anything *emitted* it.
- Produced `Measurement` values from the request path. The measurement-with-provenance model, one of
  the product's stated differentiators, was fully built and tested but used only by tests: durations
  reached metrics as bare histogram values with no provenance, and the exchange record carried none
  at all. Latency phases now carry `Measured`, token counts keep `UpstreamReported`, and no
  throughput value is derived, because a non-streamed exchange has no boundary separating prompt
  processing from generation.
- Added stable log event identifiers. `docs/OBSERVABILITY.md` requires them and there were none; a
  message is prose that will be reworded, while an identifier is what an alert rule can match on.
- Removed a service-locator call from the observability registration. The activity listener is now a
  constructor dependency of the telemetry, so "spans exist before the first one is started" is a
  compile-time fact rather than a registration ordering convention.
- Corrected `CLAUDE.md` and `docs/ARCHITECTURE.md` to put the structural summary before model
  resolution, matching the implementation. A request naming an unknown model is the case an operator
  most needs evidence for, and resolving first leaves it with no record of what arrived.
- Recorded three further decisions in ADR 0008: the request-path reordering, the rule that declared
  vocabulary must have a producer, and the deliberate process-global mutation of the activity
  identifier format.

### Stage 1A — Observability and published contracts

- Added spans and metrics through `System.Diagnostics` alone. No OpenTelemetry SDK is referenced and
  an architecture test enforces that; these are the primitives the SDK itself consumes, so adopting
  it later adds an exporter rather than rewriting instrumentation.
- Registered AgentSplice's own `ActivityListener`, which is what makes tracing exist at all.
  `StartActivity` returns null when nothing subscribes to a source, and with no SDK nothing does — so
  every span would have been absent and `x-agentsplice-trace-id` could never have been populated,
  despite the API contract promising it. The W3C identifier format is forced because the domain's
  `TraceId` accepts only 32 lowercase hexadecimal characters.
- Declared only the instruments this stage can honestly emit. Every streaming instrument, the
  first-byte and first-event timings, and both throughput instruments are absent, because a
  non-streamed exchange offers no boundary to measure them against and a zero would read as "this
  happened, and it was none".
- Classified success and failure from the recorded upstream status class rather than from the absence
  of an error, so a relayed upstream 500 is never counted as a success.
- Kept model identifiers out of metric dimensions. They are client-supplied and unbounded, so one
  caller could otherwise multiply the cardinality of every series without limit.
- Closed a credential leak that had nothing to do with AgentSplice's own logging:
  `IHttpClientFactory` writes request headers at `Trace`, so the runtime's bearer token reached any
  enabled sink verbatim. The provider's clients now redact credential-bearing headers. Found by the
  privacy suite, which runs at `Trace` precisely so that "content is absent even at the most verbose
  setting" is the claim being made.
- Added the privacy suite: four distinct sentinels for prompt, completion, tool argument, and API
  key, asserted absent from every log channel — message, structured state, and scope — because a
  structured value reaches a sink just as surely as a formatted message.
- Published the `error.type` vocabulary and the observability names in their documents, bound by
  contract tests, so code and specification cannot drift.
- Updated the OpenAPI draft to describe what the gateway actually does: `text/event-stream` removed
  and `stream` constrained to `false` until Stage 1B, a closed `error.type` enum so a later category
  is a visible schema change, correlation headers on every error response, the statuses the gateway
  can return, and the `created` sentinel's meaning stated where a reader will find it.
- Added ADR 0008 recording all eighteen Stage 1A decisions with the alternatives rejected.

### Stage 1A — Failures, limits, and credential containment

- Consolidated error translation into a single table, total over `FailureClass`, so a new failure
  class cannot be added without deciding what it means to a client. Spread across call sites the
  same mapping drifts, and two places translating one condition into two statuses is a defect
  neither place looks wrong for.
- Published the `error.type` vocabulary. The specification supplies exactly one by example, so
  Stage 1A defines the rest: client-validation failures reuse OpenAI's `invalid_request_error` so an
  SDK that branches on `type` keeps working, and the gateway-only conditions get categories a plain
  model provider has no vocabulary for.
- Fixed a defect in the completion path: a client disconnect was recorded as a failure rather than a
  cancellation. The provider catches the cancellation itself and reports it as a classified result,
  so the exchange never reached `Cancelled` — which would have made a real disconnect
  indistinguishable from a runtime fault in both the exchange list and the metrics.
- Proved cancellation reaches the runtime. The test waits on the fake upstream's own abort signal,
  because anything less only shows that AgentSplice stopped reading, which a client experiences
  identically while the runtime keeps generating and burning the compute cancellation exists to
  reclaim.
- Verified timeout-phase attribution against a real listener, including that a runtime which
  accepted the connection and went quiet is never reported as unreachable.
- Verified credential containment end to end: the configured key is attached, a client's own
  `Authorization` never replaces it, and the key appears in no response, error, or header. An
  upstream authentication body is discarded rather than relayed, because it can hint at the key's
  shape.
- Verified the header allowlists in both directions, including that an invented client header and an
  upstream `Set-Cookie` are dropped while `Retry-After` and rate-limit headers are relayed.
- Added a last-resort exception handler so a fault escaping the pipeline still produces the stable
  envelope rather than a framework page that could disclose a stack trace.
- Diagnosed the intermittent integration failure reported in the previous slice. It was not
  contention: `WebApplicationFactory` resolves a top-level-statements entry point through static
  handoff state shared across the process, so concurrent hosts let one factory observe another's
  disposed provider, and the tests that deliberately fail startup saw `ObjectDisposedException`
  instead of the validation failure they assert. Collections now run serially in that assembly,
  which closes the window rather than narrowing it.

### Stage 1A — Non-streaming completions

- Added `POST /v1/chat/completions` for non-streaming requests (FR-CHAT-001, FR-CHAT-003,
  FR-CHAT-004, FR-CHAT-009, FR-CHAT-010, FR-CHAT-014, FR-CHAT-015). A standard client can now
  complete a request through AgentSplice.
- Forwarding is byte-preserving. The runtime receives the client's original bytes unless routing
  renames the model, in which case only the bytes of the top-level `model` value are replaced.
  The body is spliced using offsets recorded during the same pass that builds the structural
  summary, rather than reparsed and re-emitted: a JSON writer normalises escape forms and number
  formatting, so `A` would become `A` and `1.0` would become `1`, and an exact-forwarding test
  built on that would only prove that our own parser round-trips.
- Made `ExchangeStatus.Completed` mean the transport cycle finished, not that the operation
  succeeded. A relayed 429 or 500 completes with no failure class, because AgentSplice did not fail;
  the runtime's own status is recorded separately and is what success and error are classified from.
- Added `UpstreamResponseMetadata` to the domain. Status is transport metadata, not body structure,
  and the two come apart exactly when it matters: a 204 has no body, a `429 text/plain` has one that
  is not protocol data, and a truncated 500 has one that cannot be parsed. Attaching status to the
  structural summary would have lost it in all three cases.
- Bounded the cardinality of recorded role names and made every truncation visible. The summary
  already bounded each name's length but not how many distinct names it held, so a request with a
  unique role per message would have grown it without limit — defeating the bound's own rationale.
  Folded roles still sum to the message count, and silent truncation no longer reads as completeness.
- Rejected `stream: true` with a stable message that names no roadmap stage, since the message is a
  public contract that outlives the stage. Buffering an event stream into a JSON body would be an
  invisible semantic transformation, and a `200` would make an unimplemented capability look
  implemented.
- Rejected a repeated `model`, `messages`, or `stream`. "Last wins" can differ between AgentSplice's
  validation, the splice arithmetic, and the runtime's parser, so the three could disagree about
  what was actually sent.
- Added header allowlists in both directions. The client's `Authorization` is never forwarded, no
  hop-by-hop header is copied, and a relayed 429 keeps its `Retry-After` — without which the status
  conveys nothing actionable.
- Built the structural summary *before* resolving the model, so a request naming an unknown model
  still leaves safe evidence of what arrived.
- Added `IExchangeRecordSink` with a discarding default. It is the only way Stage 1A's timeline is
  observable before persistence exists, so the "routing changes are represented as events" exit
  criterion would otherwise be untestable, and it is the seam Stage 1C implements.
- Fixed a Stage 0 validator defect: an identity alias, mapping a model name to itself on a chosen
  runtime, was rejected as a resolution cycle. The resolver does not chain alias to alias, so it
  terminates immediately — and an identity alias is the only way an operator can pin a model to one
  runtime when two offer it, so rejecting it removed the sole deterministic override of the
  FR-MOD-004 tie-break. Multi-alias cycles are still rejected.
- Capped integration-suite parallelism. Nearly every test binds a real listener and boots a host, and
  unbounded parallelism made the suite compete with itself, which surfaced as a startup test failing
  for a reason other than the one it asserts.

### Stage 1A — Model discovery and routing

- Added `GET /v1/models`, composing configured aliases with models discovered from enabled runtimes
  (FR-MOD-001, FR-MOD-002). The first HTTP surface the gateway serves.
- Added deterministic model resolution (FR-MOD-005): an enabled alias, then a discovered model, then
  an optional configured pass-through runtime. Aliases resolve without any network call, so an
  alias-only deployment never pays for discovery on the request path.
- Distinguished "the catalogue was consulted and the model is absent" from "no runtime could be
  asked". The first is a 404, the second a 502. Reporting a model as missing when the truth is that
  AgentSplice could not ask is precisely the misleading evidence this product exists to remove.
- Separated *a routing decision was made* from *the forwarded body must change*. An alias that
  selects a runtime without renaming the model, a duplicate identifier resolved by tie-break, and a
  pass-through are all routing decisions FR-TRACE-007 requires to be visible, and none of them
  changes a byte. `ModelResolution.IsRoutingChange` answers only the second question.
- Added per-runtime discovery caching with the configured window, the stale-serve policy, and
  refresh coalescing (FR-MOD-003). A failed refresh is remembered for the same window: without that,
  every request naming an unknown model would wait out the connect timeout again while a runtime is
  down. A failed refresh never destroys the catalogue it failed to replace.
- Made model identifiers opaque. They previously had to match a punctuation allowlist, which would
  have rejected values a runtime would have accepted and made AgentSplice the source of a failure
  that does not exist downstream. Validation now bounds length and rejects only control characters
  and text that cannot be encoded as UTF-8.
- Kept creation times honest. `created` is a Unix timestamp, so zero is a claim about 1970 rather
  than a way of saying "unknown". The catalogue holds `null`, an alias inherits the evidence of the
  model it targets, and the compatibility sentinel the OpenAI schema forces exists only inside the
  response writer.
- Added `agentsplice:defaultRuntimeId`, validated to name a configured *enabled* runtime, which
  makes `ModelResolutionSource.PassThrough` reachable and keeps a discovery-disabled runtime
  routable. Unset by default.
- Added `agentsplice:limits`, bounding request, completion, and catalogue bodies. Mandatory because
  the non-streaming path is deliberately fully buffered, which without a ceiling turns one defective
  runtime into gateway-wide memory pressure.
- Added the LM Studio provider with one named HTTP client per runtime. `ConnectTimeout` is a
  property of the handler while `timeouts:connect` is configured per runtime, so a shared handler
  could honour only one runtime's budget; per-runtime clients also isolate connection pools.
  `HttpClient.Timeout` is infinite, because the 100-second default throws a cancellation
  indistinguishable from a client disconnect and would make timeout-phase attribution impossible.
- Contained upstream credentials. They are resolved inside the provider at the moment the request is
  built rather than carried through orchestration, and `RuntimeCredential.ToString()` returns a
  placeholder so an accidental `{Credential}` in a log template cannot leak a key. Redirects and
  system proxies are disabled so a bearer token cannot be sent to a host the operator did not name.
- Enforced two new boundaries by test: `AgentSplice.Application` may not reference
  `System.Net.Http`, which forces transport classification into the provider; and the API project
  may not parse protocol JSON, record exchange evidence, or open its own connections, which is the
  checkable form of "no orchestration in endpoint lambdas".

Measurements in this slice are taken against the deterministic fake upstream and are fixture
measurements, not hardware claims. Stage 1A makes no compatibility claim; that requires a
conformance report.

### Stage 0 — Repository foundation

- Bootstrapped the .NET 8 solution with the seven Stage 1 production projects and enforced the
  dependency rules from `docs/ARCHITECTURE.md` with architecture tests.
- Pinned the SDK feature band, centralised package versions, promoted warnings to errors, and moved
  formatting enforcement to a separate CI step (ADR 0007).
- Added the Stage 0 domain model: exchanges, append-only timelines, observations with bounded
  sanitised detail, measurements with mandatory provenance, and throughput derivation that returns
  unknown rather than zero when the evidence is insufficient.
- Added typed configuration with startup validation, including duplicate and cyclic alias detection
  (FR-MOD-006), timeout-phase coherence, credential-free runtime URLs, and a guard that refuses to
  start when the Stage 4 adapter flag is enabled.
- Added stable Stage 1 error codes and the normative OpenTelemetry names, both verified against the
  documents that declare them by contract tests.
- Added the deterministic fake upstream runtime: a real loopback Kestrel listener with byte-exact SSE
  scripting, arbitrary chunk boundaries, delayed headers, trailing stalls, premature close, request
  recording, and upstream-side cancellation observation.
- Added Windows and Ubuntu CI, a real multi-stage Dockerfile running as a non-root user, a
  `.dockerignore` that keeps host build output out of the image context, and loopback-bound Compose
  defaults.
- Made the loopback binding default a fallback rather than an `appsettings.json` value. Declared in the
  settings file it layered over `ASPNETCORE_URLS`, so the container bound loopback inside itself and
  the published port mapping was unreachable.
- Corrected the deployment environment variables to the double-underscore form that actually binds to
  the `agentsplice` configuration section, and added a contract test that fails when a variable binds
  to nothing.
- Declared the three Stage 1 administrative endpoints that `docs/API.md` documented but the OpenAPI
  draft omitted.
- Verified the Stage 0 exit criterion: a streamed fake-upstream exchange is fully representable by the
  domain model, with unobserved boundaries staying absent rather than becoming zero.

### Revised product thesis

- Repositioned AgentSplice from a protocol-normalizing gateway to a local-first interoperability, observability, replay, conformance, and evaluation platform.
- Defined trace, replay, conformance, evaluation, regression, and OpenTelemetry export as the durable core.
- Moved tool-call recovery, prompt/schema compaction, protocol translation, model-specific rules, and runtime workarounds to optional later-stage adapters.
- Reordered the roadmap so transparent trace proxying comes first, followed by replay/conformance, agent evaluation, adapters, and ecosystem/backend work.
- Added product-positioning, conformance, replay, and adapter-lifecycle documents.
- Updated architecture, API, security, testing, observability, benchmarking, portfolio, and implementation-agent instructions.
- Expanded the OpenAPI draft with exchange and administrative trace endpoints.

### Original specification pack

- Created specification-first repository pack.
- Defined AgentSplice name, initial architecture, roadmap, API draft, security model, and implementation instructions.
