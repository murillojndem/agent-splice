# Development guide

## Prerequisites

- .NET 10 SDK. `global.json` asks for SDK 10.0.100 or any later 10.0 SDK (`rollForward: latestFeature`)
  and builds with the newest one installed, so a machine with only another major version's SDK cannot
  build this repository until a 10.0 SDK is present (ADR 0007, ADR 0024).
- Docker Desktop or a compatible Docker Engine
- Git
- PowerShell 7 recommended on Windows
- Node.js 22 and pnpm 10, for the dashboard in `web/AgentSplice.Dashboard`. The gateway builds and
  tests without them; the dashboard is optional (FR-DASH-002) and gated by its own CI job. The
  container image builds the dashboard in its own stage, so `docker compose up --build` needs neither.

Check the SDK:

```powershell
dotnet --list-sdks
```

## Build and test

```powershell
dotnet restore
dotnet build --configuration Release
dotnet test --configuration Release
```

Warnings are errors (`Directory.Build.props`), so a build that succeeds locally is a build that
passes CI's `-warnaserror` step.

Formatting is verified separately, exactly as CI does it:

```powershell
dotnet format whitespace --verify-no-changes
```

To apply formatting instead of verifying it:

```powershell
dotnet format whitespace
```

## Solution layout

```text
src/
  AgentSplice.Domain              exchanges, timelines, observations, measurement provenance
  AgentSplice.Application         validated configuration, stable error codes
  AgentSplice.Infrastructure      configuration binding and hosting-adjacent composition
  AgentSplice.Protocols.OpenAI    OpenAI ingress module
  AgentSplice.Providers.LmStudio  LM Studio provider adapter
  AgentSplice.Observability       OpenTelemetry names and instrumentation
  AgentSplice.Api                 composition root and host
  AgentSplice.HealthProbe         the container image's health check; references nothing

tests/
  AgentSplice.TestSupport         deterministic fake upstream and repository path helpers
  AgentSplice.UnitTests           domain rules, provenance, configuration validation
  AgentSplice.ContractTests       code constants versus the documents that declare them
  AgentSplice.IntegrationTests    host startup, configuration binding, fake-upstream exchanges
  AgentSplice.ArchitectureTests   module dependency and immutability rules
```

`AgentSplice.PerformanceTests` was deliberately not created; ADR 0009 records why a wall-clock
benchmark on shared runners would not have earned a place in CI, and how the no-full-buffering claim
is proven instead.

## Running the host

The host serves `GET /v1/models` and `POST /v1/chat/completions`, the `/api/v1` administrative surface
that reads retained evidence, and the `/health/live` and `/health/ready` probes.

```powershell
dotnet run --project src/AgentSplice.Api
```

An invalid configuration fails at startup rather than on the first request, so a startup failure that
names a setting is the expected behaviour, not a bug.

### The dashboard

The host also serves the dashboard at `http://127.0.0.1:5280/dashboard/`, and `/` redirects there —
but only when it finds a build, which a fresh clone does not have. Run from source as above, the
gateway logs event `1204 DashboardBuildMissing` naming where it looked, serves every API, and serves
nothing at `/dashboard/`. That is expected (FR-DASH-002, ADR 0015).

To have the gateway serve a build of your working tree, build it and point `rootPath` at it.
`dotnet run` uses the project directory as the content root, so the relative path starts there:

```powershell
pnpm --dir web/AgentSplice.Dashboard install --frozen-lockfile
pnpm --dir web/AgentSplice.Dashboard build
dotnet run --project src/AgentSplice.Api -- --agentsplice:dashboard:rootPath=../../web/AgentSplice.Dashboard/dist
```

The startup log then says `1203 DashboardServed` with the directory. A published gateway looks in
`dashboard/` beside its content root, so copy `web/AgentSplice.Dashboard/dist` to `<publish>/dashboard`
when running `dotnet publish` output directly; the container image and the packages (`Packaging`
below) do this themselves.

For working on the dashboard, `pnpm dev` is faster: it serves `http://127.0.0.1:5281/dashboard/` with
hot reload and proxies `/api` to the gateway on 5280, which reproduces the single origin
(`web/README.md`). `agentsplice:dashboard:enabled: false` turns the gateway's copy off entirely.

## Local runtime

LM Studio commonly listens at `http://127.0.0.1:1234/v1`. When AgentSplice runs in Docker Desktop,
configure the upstream as `http://host.docker.internal:1234/v1`.

## Configuration

Settings live under the `agentsplice` section of `appsettings.json`. Environment variables use the
double-underscore form that `Microsoft.Extensions.Configuration` maps onto that section:

```text
AGENTSPLICE__PERSISTENCE__MODE                 -> agentsplice:persistence:mode
AGENTSPLICE__RUNTIMES__0__BASEURL              -> agentsplice:runtimes:0:baseUrl
AGENTSPLICE__RUNTIMES__0__DISCOVERY__ENABLED   -> agentsplice:runtimes:0:discovery:enabled
```

A contract test asserts that every `AGENTSPLICE__*` variable in `docker-compose.yml` and
`.env.example` resolves to a real setting, because a variable that binds to nothing is silently
ignored and the deployment quietly runs on defaults. The same test reads every setting the user
guides name — `README.md`, `docs/INSTALLATION.md`, `docs/CLIENTS.md`, and this file — as a variable or
as a `--agentsplice:` argument.

Copy `.env.example` to `.env` for Compose. Never commit a real API key: a runtime is configured with
the *name* of the environment variable holding its key, never the value.

### Running in Docker

Compose requires an administrative token and refuses to start without one:

```bash
cp .env.example .env
# your own value, in this shell for the calls below and in .env for Compose
export AGENTSPLICE_ADMIN_API_KEY="$(openssl rand -hex 32)"
echo "AGENTSPLICE_ADMIN_API_KEY=$AGENTSPLICE_ADMIN_API_KEY" >> .env
docker compose up --build --wait
```

Omitting it fails at `docker compose config` with a message naming the variable. That is deliberate:
the container binds `0.0.0.0:5280` inside itself, and a process cannot tell that the host published
that port on loopback only — so from the gateway's point of view it is network-reachable, and
`AdministrationBindingGuard` refuses to serve stored evidence without a credential. Shipping a default
token instead would ship a token everybody knows.

Once a token is configured it is required for **every** `/api/v1` call, including one made from the
host through the published port:

```bash
curl -i http://127.0.0.1:5280/health/live                       # 204, no token needed
curl -i http://127.0.0.1:5280/api/v1/system                     # 401
curl -i -H "Authorization: Bearer $AGENTSPLICE_ADMIN_API_KEY" http://127.0.0.1:5280/api/v1/system  # 200
```

See `docs/SECURITY.md` for why loopback alone does not authorise: behind a reverse proxy every
relayed request arrives from `127.0.0.1`.

The image carries the dashboard. Open `http://127.0.0.1:5280/`, which redirects to `/dashboard/`, and
paste the token into the field at the top of the page: the page itself needs no credential, the
evidence it shows does, and the page keeps the token in memory only, so a new tab asks again.

The image also reports its own health (ADR 0016). Its `HEALTHCHECK` runs
`AgentSplice.HealthProbe`, which asks `GET /health/live` and exits 0 or 1; `--wait` above returns once
that reports healthy, and fails if it reports unhealthy. To see the status and what the probe said:

```bash
docker compose ps agentsplice                                   # STATUS ... (healthy)
docker inspect --format '{{json .State.Health}}' "$(docker compose ps --quiet agentsplice)"
```

The check probes port 5280 inside the container, the port `ASPNETCORE_URLS` names. A deployment that
moves the gateway to another container port must override `healthcheck.test` to match; the host side
of the port mapping does not matter. Docker only reports health: outside Swarm nothing restarts an
unhealthy container.

CI builds the image and drives it the same way. To run that check against a local container:

```bash
bash .github/scripts/smoke-gateway.sh http://127.0.0.1:5280 "$AGENTSPLICE_ADMIN_API_KEY"
```

Running directly rather than in a container — from source, or a package — needs none of
this: the default binding is loopback and no token is configured, so `/api/v1` serves local callers
and startup refuses any binding that would change that. `docs/INSTALLATION.md` explains why setting a
token is still worthwhile there.

### Seeing what AgentSplice exports

Traces and metrics are exported over OTLP only when asked to. The Compose file carries a collector
that prints what it receives, reachable on the Compose network only:

```bash
# in .env
AGENTSPLICE__TELEMETRY__OTLP__ENABLED=true

docker compose --profile otel up --build
docker compose logs -f otel-collector
```

Running directly, point the gateway at any OTLP receiver — for example the same collector image run
with its HTTP port published on loopback:

```bash
AGENTSPLICE__TELEMETRY__OTLP__ENABLED=true \
AGENTSPLICE__TELEMETRY__OTLP__ENDPOINT=http://127.0.0.1:4318 \
dotnet run --project src/AgentSplice.Api
```

Spans arrive within about five seconds of an exchange and metrics within a minute. Setting
`OTEL_EXPORTER_OTLP_ENDPOINT` does nothing; see `docs/OBSERVABILITY.md` "OpenTelemetry export".

### Packaging

The packages are self-contained, single-file builds with the dashboard beside them, for machines with
no .NET installed: win-x64 for Windows and linux-x64 for x64 distributions on glibc (ADR 0017, ADR 0021;
installing them is in `docs/INSTALLATION.md`). Building one needs the .NET 10 SDK, Node 22, pnpm 10, and
PowerShell 7.4 or later, and the Linux package is built on Linux, whose smoke test also uses GNU tar,
`ss` from iproute2, bash, and curl:

```powershell
# Windows
pwsh -File .github/scripts/package.ps1 -RuntimeIdentifier win-x64 -OutputDirectory artifacts/package
pwsh -File .github/scripts/smoke-package.ps1 -Archive (Get-Item artifacts/package/*.zip).FullName

# Linux
pwsh -File .github/scripts/package.ps1 -RuntimeIdentifier linux-x64 -OutputDirectory artifacts/package
pwsh -File .github/scripts/smoke-package.ps1 -Archive (Get-Item artifacts/package/*.tar.gz).FullName
```

The first script builds the dashboard, publishes the gateway with the profile named for the platform —
`src/AgentSplice.Api/Properties/PublishProfiles/win-x64.pubxml` or `linux-x64.pubxml` — copies the
dashboard to where the shipped `rootPath` looks, adds the license and the notices (ADR 0020), and writes
`agentsplice-<version>-win-x64.zip` or `agentsplice-<version>-linux-x64.tar.gz`, and `SHA256SUMS`. The
tar.gz records owner and group 0, the executable's execute bit, and modes nobody but the owner can
write; it is written only on Linux, where the published executable has that bit. Everything specific to
a package lives in its profile, never in the project file, and a contract test holds that line: a
runtime identifier or `SelfContained` there would change the container publish and break the test
projects. Each profile also sets the runtime switch that makes the executable's directory the content
root, so a package finds its settings and dashboard wherever it is started from; no other build carries
it, so `dotnet run` and the image behave exactly as before.

The second script is what CI's `package-windows` and `package-linux` jobs run. It verifies the archive
against `SHA256SUMS`, checks what the package contains — and on Linux what the archive's listing
records — then starts it twice from an empty folder elsewhere: once with the shipped defaults, checking
the dashboard, `/api/v1` without a token, an exchange read back from the store, the store beside the
executable, nothing written to the working directory, and a listener on loopback only; and once with a
token, running `smoke-gateway.sh` through Bash, which on Windows is Git Bash. Port 5280 must be free.

A package carries the .NET runtime of the SDK that built it, and nothing patches that runtime
afterwards, so a package built locally with an old 10.0 SDK carries an old runtime. Releases are built
by CI with the current 10.0 SDK, and `README.txt` in the package names the runtime version. Microsoft
supports .NET 10 until November 14, 2028.

CI's `release-assets` job then assembles what a release publishes from the two package artifacts and
the image's archive: it verifies each, combines their digests into one `SHA256SUMS`, checks that the set
is one archive per platform and the image's, all of one version, and uploads `agentsplice-release`. It
runs on every change, so a defect in assembling a release fails a pull request.

## Releasing

A release is a tag, what the tag produces is a draft for a maintainer to review, and only publishing
the draft pushes the image (ADR 0018, ADR 0022):

1. Set the version in `Directory.Build.props` (`VersionPrefix` and `VersionSuffix`) and the same value
   in `web/AgentSplice.Dashboard/package.json`; a contract test fails if the two differ, and another if
   `docs/INSTALLATION.md` or `README.md` names another. From the push of that commit until the release
   is published, the download addresses and the image tag both documents give name a release that does
   not exist yet, and the README is the repository's front page: publish the draft without delay. Move
   the `Unreleased` entries in `CHANGELOG.md` under a heading for the version, `## <version> — <date>`;
   no test checks that move, so read the heading against the tag. Leave the current stage `in progress`
   in `docs/ROADMAP.md`: the release is part of the stage, `ImplementedStageContractTests` requires the
   stage the build reports to be the one the roadmap marks in progress, and marking it complete is a
   commit of its own after the release.
2. Commit that on `main`, then tag the commit `v<version>` exactly, for example `v0.2.0-alpha`, and push
   the tag. Tag only a reviewed commit on `main`: a tag runs the workflows of the commit it names, as they
   are there, with whatever permissions they ask for. Release immutability is on, so a tag whose release
   has been published can never be used again; until then, a draft and its tag can still be deleted and
   made again.
3. `.github/workflows/release.yml` checks that the tag names the build's version and that `LICENSE`,
   `NOTICE`, and `THIRD-PARTY-NOTICES.md` exist (ADR 0020), runs the whole CI suite against the tag, and
   creates a **draft pre-release** from the `agentsplice-release` artifact that run assembled: the
   Windows and Linux packages it built and smoke-tested, the image as the archive it built, ran, and
   ran again from the archive, and one `SHA256SUMS` for all three (ADR 0021, ADR 0022). Nothing is
   visible to anyone else yet, and nothing has reached the registry.
4. Review the draft — download the assets and check them against `SHA256SUMS`, read the notes — and
   publish it on GitHub.
5. Publishing starts `.github/workflows/publish-image.yml`. It pushes the archive the release carries to
   `ghcr.io/<owner>/<repository>:<version>`, never `latest`, and writes the registry's digest into the
   release notes. Check that run, then:
   - the package page shows the image as public, linked to this repository, with its description and
     license — the first push creates the package, and only then can that be seen;
   - `docker pull ghcr.io/<owner>/<repository>@sha256:<digest>`, the reference the notes now end with,
     works without signing in;
   - `gh release verify v<version>` and `gh release verify-asset v<version> <file>` accept the release
     and its assets. This needs a recent GitHub CLI: 2.76 found no attestation for 0.2.0-alpha, and 2.102
     verified it.

If `publish-image.yml` fails, its log names the check that stopped it; nothing is pushed until the
release and its archive have passed theirs.

- **The image is not anonymously readable.** The package was created private. In the package's settings,
  make it public — that cannot be undone — and check that it is linked to this repository. Then re-run the
  failed jobs: the push finds the tag already naming this image and pushes nothing, checks anonymous
  access again, and `notes` writes the digest.
- **The release is not immutable.** Release immutability was off when it was published, so its archive
  could still be replaced, and the image is not pushed from it. Turn immutability on, delete the release
  and its tag — a release that is not immutable allows both — and release the version again.
- **It cannot tell whether the tag exists.** The registry failed the lookup three times, the last time
  with neither the tag's manifest, "not found", nor a refusal. Re-run the failed jobs once it answers
  again.
- **A cause in the workflow itself.** Fix it on `main` and run it for the tag with
  `gh workflow run publish-image.yml -f tag=v<version>`: a release event runs the file as it was at the
  tag, and a dispatch runs `main`'s.

Every way in, it refuses a release that is not published, that GitHub Actions did not create, or that is
not immutable, and it never moves a tag that already names another image.

To retry a release before it is published, delete the draft and the tag, fix the cause, and tag again:
`gh release create` refuses a tag that already has a release. After publication, a release's tag is
never reused: the fix is a new version. Vulnerability reports arrive through GitHub's private
vulnerability reporting, which stays enabled (`SECURITY.md`).

## Adding a package

Versions are centrally managed. Add a `PackageVersion` to `Directory.Packages.props`, then reference
the package without a version in the project file.

A package the gateway ships, and a runtime dependency of the dashboard, must be under a license
compatible with Apache-2.0 and must be listed in `THIRD-PARTY-NOTICES.md` with its license and
copyright, in the same change (ADR 0020). `LicenseContractTests` fails until it is. It reads the
gateway's restored graph, `src/AgentSplice.Api/obj/project.assets.json`, so restore the solution before
running the contract tests on their own. Every package there that puts a runtime, native, or resource
file into the build must be in the Packages column of the notices' Components table, by its name or by a
`.*` family; analyzers and other build-time packages put no file in and are not listed. The dashboard's
production graph, walked through `pnpm-lock.yaml`, must be listed the same way. A package that is in a
graph but never ships goes under "Packages that do not ship" instead.

The dashboard's source imports only from its `dependencies`, which a contract test also checks: the
bundler takes whatever `src/` imports, whichever section of `package.json` declares it. The build tools
in `devDependencies` — Vite, Rollup, esbuild — have a row of their own in the notices, because the code
they generate is part of the bundle.

Restore audits the packages `Directory.Packages.props` names for known vulnerabilities, and an advisory
against one fails the build (`NU1901` to `NU1904`), as any warning does. Move the package to a version
the advisory says is fixed. If there is none yet, or the advisory does not apply to how the gateway uses
the package, suppress that one advisory with `<NuGetAuditSuppress Include="<advisory URL>" />` in
`Directory.Build.props`, with a comment saying why. The build does not audit transitive packages, which
.NET 10 would by default (ADR 0024); `dotnet list package --vulnerable --include-transitive` lists what
is known against them.

## Database

SQLite is the shipped store and is sufficient for local development. `agentsplice:persistence:mode`
accepts `Sqlite` or `None`; the PostgreSQL Compose profile remains a planning scaffold, because
FR-DATA-003 commits to PostgreSQL through the same contracts but no provider ships yet.

A relative `Data Source` is resolved against the content root, as `appsettings.json` and the
dashboard's `rootPath` are, so the shipped `Data Source=agentsplice.db` is
`src/AgentSplice.Api/agentsplice.db` under `dotnet run` and the file beside the executable in the
packages (ADR 0017, ADR 0021). Absolute paths, `:memory:`, `file:` URIs, and `|DataDirectory|` are used as
written. The startup log names the file as `1305 MetadataStoreOpened`. The gateway never creates the
store's directory: a missing or read-only one fails startup with a message naming the file.

Since Entity Framework Core 9 a migration first takes a lock inside the store, a row in
`__EFMigrationsLock`, and waits while it finds one. A gateway killed while it was starting can leave the
row behind; the next start then fails after 30 seconds with an error naming the store and the lock,
rather than waiting forever (`docs/INSTALLATION.md`, Troubleshooting; ADR 0024).

The schema is versioned with EF Core migrations, applied at startup before anything reads or writes.
To change it, edit the row types or `AgentSpliceDbContext`, then:

```powershell
dotnet ef migrations add <Name> --project src/AgentSplice.Infrastructure --output-dir Persistence/Migrations
```

`Microsoft.EntityFrameworkCore.Design` is referenced with `PrivateAssets="all"` for that command
alone: it is a build-time dependency and never ships. Keep the model provider-neutral — no SQLite
type names, no raw SQL, timestamps as UTC ticks — or the PostgreSQL commitment becomes a rewrite.

## Branching

Use short-lived feature branches. Reference specification requirement IDs in commits or pull requests.

## Documentation

Architecture-significant changes require an ADR. User-visible behavior changes require API and
specification updates. Contract tests fail when a published constant and its document disagree, so
documentation drift shows up as a red build rather than as stale prose.
