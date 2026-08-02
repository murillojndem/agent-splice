# Development guide

## Prerequisites

- .NET 8 SDK. `global.json` pins the 8.0 feature band, so a machine with only .NET 10 installed
  cannot build this repository until an 8.0 SDK is present (ADR 0007).
- Docker Desktop or a compatible Docker Engine
- Git
- PowerShell 7 recommended on Windows
- Node.js and pnpm only when the dashboard exists

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

Stage 0's host boots and validates configuration; it serves no HTTP endpoints. `GET /v1/models` and
`POST /v1/chat/completions` arrive with Stage 1A.

```powershell
dotnet run --project src/AgentSplice.Api
```

An invalid configuration fails at startup rather than on the first request, so a startup failure that
names a setting is the expected behaviour, not a bug.

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

## Adding a package

Versions are centrally managed. Add a `PackageVersion` to `Directory.Packages.props`, then reference
the package without a version in the project file.

## Database

SQLite is the shipped store and is sufficient for local development. `agentsplice:persistence:mode`
accepts `Sqlite` or `None`; the PostgreSQL Compose profile remains a planning scaffold, because
FR-DATA-003 commits to PostgreSQL through the same contracts but no provider ships yet.

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
