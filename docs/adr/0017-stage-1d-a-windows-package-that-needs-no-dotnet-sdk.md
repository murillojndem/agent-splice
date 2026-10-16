# ADR 0017 — Stage 1D: a Windows package that needs no .NET SDK

- Status: Accepted, extended by [ADR 0021](0021-stage-1d-a-linux-package-that-needs-no-dotnet-installation.md) (a Linux package; one script and one smoke test for both)
- Date: 2026-10-15
- Related: [ADR 0007](0007-stage-0-build-and-test-toolchain.md) (loopback as a fallback; the SDK
  pinned by feature band), [ADR 0013](0013-stage-1c-metadata-store.md) (the metadata store),
  [ADR 0015](0015-stage-1d-gateway-serves-the-dashboard.md) (the dashboard beside the content root;
  the .NET build never invokes Node), [ADR 0016](0016-stage-1d-the-image-reports-its-health.md) (the
  image's health check)

## Context

`docs/SPECIFICATION.md` section 22 makes a documented Docker and direct-process setup a condition of
the first alpha (criterion 13), and the roadmap makes Windows a first-class path. On Windows without
Docker, the only route was building from source, which needs the .NET 8 SDK, and Node and pnpm for
the dashboard.

`dotnet publish` output does not close that gap by itself, for two reasons found in the code:

- **The content root is the working directory.** `Program.cs` used `WebApplication.CreateBuilder(args)`,
  and ASP.NET Core takes the content root from the working directory unless told otherwise. The
  content root is where `appsettings.json` is read from and what `agentsplice:dashboard:rootPath`
  resolves against. A published executable started from another folder therefore finds no settings,
  validates the built-in defaults, and refuses to start with an error about a connection string the
  operator never removed.
- **The store follows the working directory even when the content root is right.** The shipped
  `Data Source=agentsplice.db` went to `UseSqlite` unchanged, and Microsoft.Data.Sqlite resolves a
  relative path against the working directory. The gateway would read its settings from one folder
  and write its evidence into another — a different one each time it was started from somewhere else.

`dotnet run` hides both: its working directory is the project directory, which is also the content
root. The container hides both too: its working directory is `/app`, and its store path is absolute.

## Decisions

### 1. A self-contained, single-file win-x64 package

The package is the gateway published with `src/AgentSplice.Api/Properties/PublishProfiles/win-x64.pubxml`:
`RuntimeIdentifier` `win-x64`, `SelfContained`, `PublishSingleFile`. It carries its own .NET runtime,
so it runs on a machine with no .NET installed.

- **Not trimmed.** ASP.NET Core and EF Core are not trim-safe on .NET 8, and with warnings as errors
  the trim analyser would fail the build.
- **No ReadyToRun and no compression.** Neither is needed for a local gateway, and each changes what
  the executable is in a way the smoke test would have to prove again.
- **Native libraries beside the executable.** `e_sqlite3.dll` is a file next to `AgentSplice.Api.exe`
  rather than extracted to a temporary directory at every start.
- **No IIS assets.** The package is a process, not an IIS site, so it has no `web.config` and no IIS
  module.
- **Settings stay a file.** `appsettings.json` is beside the executable, where an operator can read it;
  `appsettings.Development.json` is not shipped.
- **Symbols are embedded.** The package script publishes with `DebugType=embedded`, so a stack trace
  from the single file keeps its line numbers and the package has no loose `.pdb` files.

Everything specific to the package lives in the profile and the script, never in the project file or
`Directory.Build.props`. A runtime identifier or `SelfContained` there would change the container
publish and break the test projects that reference the gateway. The executable keeps its name,
`AgentSplice.Api.exe`, because renaming the assembly would rename the container's entry point too.

### 2. The package's content root is the executable's directory

The profile writes a runtime switch, `AgentSplice.Hosting.ContentRootIsApplicationDirectory`, into the
package's runtime configuration. `ContentRootDefault` in `AgentSplice.Api` reads it before the host is
built. With it set, the builder's content root is `AppContext.BaseDirectory`; without it, the builder
gets exactly the options `CreateBuilder(args)` would have used. No other build carries the switch — not
`dotnet run`, not the image, not the test host — so nothing else changes.

An explicit content root still wins: `--contentRoot` on the command line, or `ASPNETCORE_CONTENTROOT`
or `DOTNET_CONTENTROOT`. Because `WebApplicationOptions.ContentRootPath` overrides all of them, the
decision reads the same sources the host reads before choosing, rather than overriding them
whenever the switch is on.

### 3. A relative store path resolves against the content root, in every build

`SqliteStoreLocation` resolves a relative `Data Source` against `IHostEnvironment.ContentRootPath`
before the context is configured. That is how every other relative path in the settings is read. It
returns these as written, the same string instance: an absolute path, an empty data source, `:memory:`
or `Mode=Memory`, a `file:` URI, and a path through `|DataDirectory|`. Each of those already names
its own location.

This is a change in behaviour wherever the working directory and the content root differ: the
package, an explicit `--contentRoot`, and a Windows service, whose working directory is `System32`. It
changes nothing for `dotnet run`, where they are the same directory, or for the image, whose path is
absolute.

### 4. The store says where it is, and fails naming the file

Once migrated, the store logs `1305 MetadataStoreOpened` (Information), naming the file. A relative
path is resolved for the operator now, so they should not have to work out which directory that was.
It logs the data source only, never the connection string, which may hold a password.

A store whose directory is missing or not writable fails startup, as before, but with an error naming
the file and saying that its directory must exist and be writable: SQLite's own message, "unable to
open database file", names nothing. The gateway still creates no directory, so a mistyped path cannot
quietly grow a tree.

### 5. The package is built outside MSBuild and smoke-tested on Windows

`.github/scripts/package-windows.ps1` builds the dashboard with pnpm, publishes with the profile, and
copies the dashboard to the directory named by the shipped `rootPath`, read from `appsettings.json` so
the two cannot drift. It then writes `README.txt`, zips `agentsplice-<version>-win-x64.zip` with a
single top-level folder, and writes `SHA256SUMS` in `sha256sum` format. The version is the one the
build stamps. As with the image, the .NET build never invokes Node (ADR 0015).

`README.txt` names the .NET runtime the package carries, because a self-contained package is never
serviced by Windows Update. Its runtime is the one the building SDK bundles, so CI builds releases with
the current .NET 8 SDK.

A new `package-windows` CI job on `windows-latest` builds the package and runs
`.github/scripts/smoke-windows-package.ps1`, then uploads the zip and `SHA256SUMS` as the
`agentsplice-win-x64` artifact. The smoke script:

- checks the archive against `SHA256SUMS`, as users are told to, and checks what the package holds;
- starts the executable from an empty folder elsewhere with the shipped defaults, and checks the
  dashboard, `/api/v1` served to a local caller without a token, a refused request read back from the
  store, the store beside the executable, nothing written to the working directory, and a listener on
  `127.0.0.1` only;
- starts it again with a token and runs the shared gateway smoke test through Git Bash.

That shared test is `smoke-image.sh`, renamed `smoke-gateway.sh`. It gains backslash traversal vectors,
because this is the first time the dashboard is served from a Windows file system, where `\` is a
separator that the router treats as part of a segment.

### 6. Every installation path is documented in one place

`docs/INSTALLATION.md` covers Docker, the Windows package, and running from source. For the package it
covers:

- downloading, verifying the digest, removing the downloaded-file mark, and extracting under the
  user's own profile;
- running the executable, and the SmartScreen and Smart App Control prompts an unsigned executable
  meets;
- setting a token, and configuring through arguments and environment variables rather than by
  editing a file an upgrade replaces;
- where the data is, how to back it up, and how to move it;
- upgrading and uninstalling.

## Consequences

- A Windows user needs neither Docker nor .NET. The archive is about 43 MB, and the executable about
  100 MB once extracted.
- The package's runtime ages from the day it is built, and only a new release patches it. That is
  stated in `README.txt`, `docs/INSTALLATION.md`, `docs/SECURITY.md`, and the threat model.
- The package is not code signed. Its integrity rests on the published SHA-256 digest; SmartScreen
  will warn on first run, and Smart App Control may refuse it outright.
- The package's default is loopback with no token. That is the configuration the threat model's
  DNS-rebinding residual describes, so the guide recommends a token.
- A relative store path given with `--contentRoot`, or under a Windows service, now names a file under
  the content root rather than under the working directory.
- Measured on a development workstation and a CI runner, never as a performance claim.

## Tests proving these decisions

- `ContentRootDefaultTests` — the switch off gives the builder's own options, the switch on gives the
  executable's directory, and an explicit content root on the command line, in every form the
  provider accepts, or from the environment wins even then. Fails against an unconditional override.
- `WindowsPackageContractTests` — the profile's settings, and its switch named as `ContentRootDefault`
  names it. Every self-contained profile carries the switch; nothing package-specific is in the project
  file or `Directory.Build.props`; development settings are excluded; and the package script publishes
  with the profile. Fails against a renamed switch.
- `SqliteStoreLocationTests` — which store paths resolve and which come back as the same instance;
  other keywords survive; describing the store never includes a password.
- `StoreLocationTests` — a relative path from the content root creates the store there and not under
  the working directory, and 1305 names it; a deployment retaining nothing reports no store. Fails
  against a raw `UseSqlite`.
- `HostStartupTests` — a missing store directory fails startup naming the file, and is not created.
- `DashboardServingTests` — backslash traversal through the in-process server.
- The CI `package-windows` job — both smoke runs against the real package. Fails against a package
  published without the switch, which exits before answering when started from another folder.
- The CI `image` job — the store reported at `/data/agentsplice.db`.

## Alternatives considered

- **A launcher script** (`cd /d "%~dp0"`). Starting the executable itself — from Explorer, from a shell
  in another folder, from Task Scheduler — would still fail. Ctrl+C would end in "Terminate batch job
  (Y/N)?", and the package would have two entry points to document.
- **`Directory.SetCurrentDirectory` at startup.** Hidden process-wide state, and it would silently
  change what a relative path given on the command line means.
- **The executable's directory as the content root in every build.** `dotnet run` would read its
  settings from `bin/`, and the documented development path to a dashboard build, relative to the
  project directory, would break.
- **`AppDomain.SetData("DataDirectory", …)` for the store.** Global mutable state, and it changes what
  every relative path means without saying so; the explicit resolution is visible and tested.
- **A framework-dependent zip.** It needs the ASP.NET Core 8 runtime installed, which is the
  prerequisite this package exists to remove; it remains documented under "From source".
- **Trimming, ReadyToRun, compression, or a single file with every native library inside.** Rejected
  in decision 1.
- **An MSI, winget, or Scoop package, or a Windows service.** Each adds an installer or a lifecycle to
  maintain before the alpha has users to ask for one; a zip and a documented procedure come first.
