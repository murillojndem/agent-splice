# Development guide

## Prerequisites

- .NET 8 SDK. `global.json` pins the 8.0 feature band, so a machine with only .NET 10 installed
  cannot build this repository until an 8.0 SDK is present (ADR 0007).
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
when running `dotnet publish` output directly; the container image and the Windows package
(`Packaging for Windows` below) do this themselves.

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
ignored and the deployment quietly runs on defaults.

Copy `.env.example` to `.env` for Compose. Never commit a real API key: a runtime is configured with
the *name* of the environment variable holding its key, never the value.

### Running in Docker

Compose requires an administrative token and refuses to start without one:

```bash
cp .env.example .env
# choose your own value and put it in .env
echo "AGENTSPLICE_ADMIN_API_KEY=$(openssl rand -hex 32)" >> .env
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

Running directly rather than in a container — from source, or the Windows package — needs none of
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

### Packaging for Windows

The Windows package is a self-contained, single-file win-x64 build with the dashboard beside it, for
machines with no .NET installed (ADR 0017; installing it is in `docs/INSTALLATION.md`). Building it
needs the .NET 8 SDK, Node 22, and pnpm 10, and PowerShell 7.2 or later:

```powershell
pwsh -File .github/scripts/package-windows.ps1 -OutputDirectory artifacts/package
pwsh -File .github/scripts/smoke-windows-package.ps1 -Archive (Get-Item artifacts/package/*.zip).FullName
```

The first script builds the dashboard, publishes the gateway with
`src/AgentSplice.Api/Properties/PublishProfiles/win-x64.pubxml`, copies the dashboard to where the
shipped `rootPath` looks, and writes `agentsplice-<version>-win-x64.zip` and `SHA256SUMS`. Everything
specific to the package lives in that profile, never in the project file, and a contract test holds
that line: a runtime identifier or `SelfContained` there would change the container publish and break
the test projects. The profile also sets the runtime switch that makes the executable's directory the
content root, so the package finds its settings and dashboard wherever it is started from; no other
build carries it, so `dotnet run` and the image behave exactly as before.

The second script is what CI's `package-windows` job runs. It verifies the archive against
`SHA256SUMS`, checks what the package contains, then starts it twice from an empty folder elsewhere:
once with the shipped defaults, checking the dashboard, `/api/v1` without a token, an exchange read
back from the store, the store beside the executable, nothing written to the working directory, and a
listener on loopback only; and once with a token, running `smoke-gateway.sh` through Git Bash. Port
5280 must be free.

A package carries the .NET runtime of the SDK that built it, and nothing patches that runtime
afterwards, so a package built locally with an old 8.0 SDK carries an old runtime. Releases are built
by CI with the current 8.0 SDK, and `README.txt` in the package names the runtime version.

## Releasing

A release is a tag, and what it publishes is a draft for a maintainer to review (ADR 0018):

1. Set the version in `Directory.Build.props` (`VersionPrefix` and `VersionSuffix`) and the same value
   in `web/AgentSplice.Dashboard/package.json`; a contract test fails if the two differ. Move the
   `Unreleased` entries in `CHANGELOG.md` under a heading for the version.
2. Commit that on `main`, then tag the commit `v<version>` exactly, for example `v0.2.0-alpha`, and push
   the tag.
3. `.github/workflows/release.yml` checks that the tag names the build's version and that a `LICENSE`
   file exists, runs the whole CI suite against the tag, and creates a **draft pre-release** with the
   Windows package and `SHA256SUMS` that run built and smoke-tested. Nothing is visible to anyone else
   yet.
4. Review the draft — the assets, their digests, the notes — and publish it on GitHub.

The image is not pushed to a registry; a release's image is built from its tag. To retry a failed
release, delete the draft and the tag, fix the cause, and tag again: `gh release create` refuses a tag
that already has a release.

## Adding a package

Versions are centrally managed. Add a `PackageVersion` to `Directory.Packages.props`, then reference
the package without a version in the project file.

## Database

SQLite is the shipped store and is sufficient for local development. `agentsplice:persistence:mode`
accepts `Sqlite` or `None`; the PostgreSQL Compose profile remains a planning scaffold, because
FR-DATA-003 commits to PostgreSQL through the same contracts but no provider ships yet.

A relative `Data Source` is resolved against the content root, as `appsettings.json` and the
dashboard's `rootPath` are, so the shipped `Data Source=agentsplice.db` is
`src/AgentSplice.Api/agentsplice.db` under `dotnet run` and the file beside the executable in the
Windows package (ADR 0017). Absolute paths, `:memory:`, `file:` URIs, and `|DataDirectory|` are used as
written. The startup log names the file as `1305 MetadataStoreOpened`. The gateway never creates the
store's directory: a missing or read-only one fails startup with a message naming the file.

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
