# ADR 0024 — Stage 1D: .NET 10 LTS before the first alpha

- Status: Accepted
- Date: 2026-10-17
- Related: [ADR 0007](0007-stage-0-build-and-test-toolchain.md) (the toolchain, whose SDK pin moves),
  [ADR 0015](0015-stage-1d-gateway-serves-the-dashboard.md) (the dashboard is a folder, not static web
  assets), [ADR 0017](0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md) and
  [ADR 0021](0021-stage-1d-a-linux-package-that-needs-no-dotnet-installation.md) (the packages, which
  carry the runtime they were built with)
- Refines: decision 12 of [ADR 0013](0013-stage-1c-metadata-store.md) (where the writer's shutdown flush
  runs)
- Extends: decision 3 of [ADR 0020](0020-stage-1d-the-project-is-licensed-under-apache-2.md) (ASP.NET
  Core's own license in `licenses/`)
- Supersedes: the ".NET 8" of CLAUDE.md's "Target .NET 8 and C# 12" (C# 12 stands), ADR 0007's pin of
  8.0.100, and the consequence of [ADR 0022](0022-stage-1d-the-image-is-pushed-to-ghcr-when-a-release-is-published.md)
  that the image's operating-system layer is Debian's

## Context

The first public alpha ships self-contained packages that carry the .NET runtime they were built with,
and an image built on Microsoft's ASP.NET Core base image. All of them were .NET 8. Microsoft ends
support for .NET 8, and for .NET 9, on November 10, 2026, 24 days after this decision. .NET 10 is the
long-term support release, supported until November 14, 2028. A package built on .NET 8 carries a
runtime that nothing patches after that date, so the threat model's "unserviced bundled runtime" entry
would have been true of the very first release within weeks of it.

The readiness review held before the alpha recommended moving first, and the maintainer chose to. A
trial on a copy of the repository had found the cost small: five analyzer findings, one behaviour change
in .NET 10's hosting, and no pending model changes for Entity Framework Core. Review of the move itself,
which ran the .NET 8 build and the .NET 10 build side by side, found the rest: two more consequences of
that hosting change, a migration lock Entity Framework Core 9 introduced, a change in what configuration
does with a null, a restore that audits every transitive package, a label the new base image carries, and
a Linux floor the bundled SQLite library raises. Each is below. CLAUDE.md pinned
.NET 8 and C# 12, and ADR 0007 pinned the 8.0 SDK, so the move needs its own decision.

## Decisions

### 1. .NET 10 LTS, before the first release

`global.json` pins `10.0.100` with `rollForward: latestFeature`, the rule ADR 0007 set for 8.0, and
every project targets `net10.0`. Microsoft's packages move to their 10.0 releases: Microsoft.Extensions,
Entity Framework Core, and the test host to 10.0.13, and the test time provider to 10.11.0.

.NET 9 is not an option, because its support ends the same day as .NET 8's. Targeting both 8.0 and
10.0 is not one either: nothing ships on 8.0 after the move, and it would double every build to keep a
runtime alive that is about to lose support.

### 2. The language stays C# 12

The runtime is a support decision, and the language is not part of it. C# 14 is .NET 10's default, and
`LangVersion` stays 12, so the compiler applies the same language rules to the same source. C# 14 makes
`field` a keyword inside property accessors, among other changes. What the move does change is the
libraries that source binds to and the runtime it runs on, and decisions 5 to 9 are where that mattered.
Moving the language is a separate decision with its own review.

### 3. The image moves to Ubuntu 24.04 LTS, named in its tag

Microsoft's released .NET 10 images are Ubuntu, Alpine, Azure Linux, and Windows; none is Debian, which
the .NET 8 image was. The build and runtime stages use `sdk:10.0-noble` and `aspnet:10.0-noble`. Today
those are the images the floating `10.0` tags point to, but because the tags name the distribution, the
operating-system layer is the one the Dockerfile asks for, and THIRD-PARTY-NOTICES.md can describe it
exactly. That layer is Ubuntu 24.04's packages, their copyright files under `/usr/share/doc`, and their
list in `/var/lib/dpkg/status`. Their sources are on Launchpad and the Ubuntu snapshot service. The
non-root `app` user (UID 1654) and `ASPNETCORE_HTTP_PORTS` are as they were.

Ubuntu's base image also labels itself `org.opencontainers.image.version=24.04`, and an image inherits
every label its Dockerfile does not set, so the gateway's image would have claimed to be version 24.04.
The runtime stage declares that label empty, and the image's tag names its version (ADR 0022). Both
image jobs in CI refuse a version label, and the image job refuses any `org.opencontainers.image` label
the Dockerfile does not declare, so the next base cannot add one unnoticed.

### 4. One version, held by a contract test

`ToolchainContractTests` reads `global.json`, `Directory.Build.props`, every MSBuild file in the
repository, the Dockerfile, and every workflow, `.yml` or `.yaml`. It fails unless the target framework,
both .NET images, and every `actions/setup-dotnet` step name the version `global.json` pins, and unless
no project file sets a target or a language of its own and `Directory.Build.props` keeps C# 12. A
setup step that names no version fails it too. It also fails unless THIRD-PARTY-NOTICES.md names the
image the runtime stage is built on. Any one of these could move alone and some build would still pass:
GitHub's runners come with 10.0 SDKs installed, so a workflow left on another SDK would not even fail,
and its package would carry whatever runtime the newest of them bundles.

### 5. The metadata writer flushes even when it never started

.NET 10's `BackgroundService.StartAsync` runs `ExecuteAsync` through `Task.Run` with the stopping token.
A stop that arrives before the thread pool picks that work up cancels it before its first line runs. The
writer's shutdown flush goes with it, and exchanges already accepted from clients and queued would be
discarded.

`MetadataPersistenceService.StopAsync` now flushes the queue itself when, and only when, the loop never
claimed it. The loop and `StopAsync` each claim the queue with a flag that is set once, so whichever
claims it first is the one reader, and the rule that one reader drains the queue still holds. A flag
rather than the state of `ExecuteTask`: a task whose work was cancelled before it ran still reads as
pending until a pool thread takes it, and on a starved pool, the one that delayed the loop, the shutdown
token can fire first.

That flush is bounded by the shutdown token, as the loop's is by `BackgroundService.StopAsync` waiting
for it, so the host's shutdown timeout still bounds how long stopping takes (ADR 0013). The writes go on
in the background past it, as the loop's do. It runs on a pool thread, as the loop does, because that is
what lets the token bound it at all: Microsoft.Data.Sqlite's asynchronous calls complete synchronously,
and it waits for a locked store by sleeping, so a flush on the stopping thread would hold it for the
store's busy timeout, per batch.

### 6. The first retention sweep finishes before the listener starts

The retention sweep runs once at startup and then on its interval. On .NET 8 that first sweep ran at the
top of `ExecuteAsync`, which in effect meant before the host started the listener: `ExecuteAsync` ran
inline until its first incomplete wait, and SQLite's asynchronous calls complete synchronously. On
.NET 10 the same code runs on the thread pool, after the listener is up. With 60,000 expired exchanges,
the .NET 10 build's first page of `/api/v1/exchanges` returned 200 of them; the .NET 8 build's returned
none.

`RetentionSweepService.StartAsync` now runs the first sweep before it starts the periodic loop, so it
finishes before the host goes on to start the listener, as it did. The price is the one .NET 8 already
charged: a long backlog delays readiness by as long as removing it takes, 12 seconds for those 60,000
exchanges. A stop during that sweep, such as Ctrl+C, fails the host's start with the cancellation, as it
did on .NET 8; so does a stop while the migration waits for its lock (decision 7).

### 7. Bringing the store up to date is bounded

Since Entity Framework Core 9 a migration first takes a lock inside the store, a row in
`__EFMigrationsLock`, on every start whether or not anything is pending, and waits for as long as it
finds one there. A gateway killed while it held the lock — ended from Task Manager, `kill -9`, a power
cut — leaves the row behind, and every later start waited forever, logging only the line it logs on
every healthy start. That is a gateway that never listens and never fails, on a store that worked with
.NET 8.

`MetadataStoreInitializer` now bounds the migration at 30 seconds, on the injected clock. Past that,
startup fails with an error that names the store and the lock and says how to delete the row when no
other gateway uses the store. Creating a store takes under a second, so the bound costs nothing today; a
future migration that could take longer on a large store must raise it.

### 8. A value-typed setting given as null fails startup

Since .NET 10 the configuration binder writes a null it finds into the property, where it used to skip
it, and JSON's `null` reaches it as a null, where it used to arrive as an empty string. For a value type
that is the type's zero value. `"persistence": {"mode": null}` binds `None` and `"capture":
{"metadataEnabled": null}` binds `false`, so either one quietly retains nothing, and the result is a
valid configuration that the options validator has no reason to refuse. On .NET 8 the same file failed
startup, because no switch or mode converts from an empty string.

`NullSettingValidator` reads the configuration the options are bound from and fails startup, naming the
setting, when a switch, a number, a duration, or a mode is null. Strings are not checked: a null string
is the setting's absence, which every optional one already means by null, and a required one is refused
as empty by the options validator. A section or a list given as null keeps its defaults, as on .NET 8.

### 9. Retention settings are bounded by what uses them

The validator required the retention windows and the sweep interval to be greater than zero and nothing
more. A window is subtracted from the current time, and the interval drives a timer that accepts
nothing under a millisecond or over 4,294,967,294 of them, so a window reaching back past the year 1 or
an interval of 60 days threw. A window is first used by the startup sweep, so it failed startup, on .NET 8
and again since decision 6, but with an exception that names no setting. The interval is first used in
`ExecuteAsync`, which on .NET 10 runs on the thread pool, so the gateway reported itself started, then
stopped, and exited with code 0.

The windows must now be at most 36,500 days, and the interval between one millisecond and 49 days, so
either one out of range fails startup with a message that names it. No other hosted service of the
gateway can throw before its first wait; this was the one that could.

### 10. Restore audits the packages this repository names, as it did on .NET 8

Restore audits packages against known vulnerabilities, and with warnings as errors an advisory fails the
build. On `net10.0` the audit covers every transitive package by default, 86 here in all rather than the
19 `Directory.Packages.props` names. `Directory.Build.props` sets `NuGetAuditMode` to `direct`, which
keeps the audit where it was. Otherwise an advisory published against any transitive package would fail
every build of an unchanged commit, a user's Compose build of a release among them, which builds from
source. `docs/DEVELOPMENT.md` says what to do when an advisory fails the build, and how to list what is
known against transitive packages.

### 11. What else .NET 10 changed around the build

- CA1873, new in .NET 10's analyzers, is waived in `.editorconfig` for the reason CA1848 is: the four
  calls it flagged each log once per start or per retention sweep, never per request.
- The Web SDK would publish an empty `AgentSplice.Api.staticwebassets.endpoints.json` beside the
  gateway. The gateway has no static web assets, because the dashboard is a folder (ADR 0015), so they
  are disabled, the packages hold what the installation guide lists, and the package smoke test refuses
  the manifest.
- ASP.NET Core's runtime pack now carries its own license file. The packages copy it into `licenses/`,
  the package script requires it, and the smoke test checks for it.
- The SDK no longer restores packages the framework provides. `System.Diagnostics.DiagnosticSource`,
  `System.Diagnostics.EventLog`, and `System.Memory` therefore no longer ship, and their row leaves
  THIRD-PARTY-NOTICES.md. SQLitePCLRaw moves from 2.1.6 to 2.1.13, whose copyright runs to 2024.
- Entity Framework Core 10 reports no pending model changes, so the migration stays as Entity Framework
  Core 8 generated it. Opening a store that an earlier build created adds Entity Framework Core 10's
  migration lock table and applies nothing.
- Two framework log events changed. Entity Framework Core writes `20411`, "Acquiring an exclusive lock
  for migration application", at Information on every start. ASP.NET Core no longer writes
  `ExceptionHandlerMiddleware`'s error for a fault an `IExceptionHandler` handled, which the gateway's
  handler does for every fault that reaches it before the response has started. An alert on those
  belongs on the gateway's own `1201 UnhandledPipelineFault`, which still carries the exception.
- .NET 10 needs OpenSSL 1.1.1 or later on Linux, where .NET 8 also took 1.0.2. Its runtime needs glibc
  2.27, but the SQLite library the Linux package bundles, SQLitePCLRaw 2.1.13, which Entity Framework
  Core 10 requires, needs glibc 2.34; 2.1.6, the .NET 8 package's, needed 2.28. So the package needs
  glibc 2.34, and no longer runs on Ubuntu 20.04, Debian 11, or RHEL 8. CI's runner has a newer glibc
  than either, so nothing there would notice the floor rising: the Linux smoke test reads the newest
  glibc any native file in the package asks for and fails past the floor the documents state, and a
  contract test holds the documents to that floor.

## Consequences

- The packages carry .NET 10, 10.0.13 when CI builds them today, and the image runs .NET 10 on Ubuntu
  24.04. The first alpha's runtime is supported until November 14, 2028.
- A machine with only an 8.0 SDK can no longer build the repository; `docs/DEVELOPMENT.md` says so.
- The Linux package needs glibc 2.34 and OpenSSL 1.1.1 or later, so it no longer runs on Ubuntu 20.04,
  Debian 11, or RHEL 8, which the .NET 8 package did; the installation guide, the package's README, and
  the release notes say so.
- ADR 0014's note that OpenTelemetry 1.19.1 needs `System.Diagnostics.DiagnosticSource` 10.0 on
  `net8.0` no longer applies, because the framework is now 10.0.
- The first trace report ([ADR 0023](0023-stage-1d-the-first-dated-trace-report.md)) ran the .NET 8.0.32
  package of `b17d169` and stays a record of that build. No report yet exercises the .NET 10 runtime the
  alpha ships.
- No request-path code changes, and no recorded evidence, metric, or configuration key changes.
  AgentSplice's own log events are unchanged; the two framework events above are not. A setting given
  as null and an out-of-range retention value now fail startup by name, where .NET 8 failed it with an
  exception from the binder, the date arithmetic, or the timer. What a client sees is unchanged, as far as the suites and the checks below
  show.

## Tests proving these decisions

- `ToolchainContractTests.Every_place_that_chooses_a_dotnet_version_chooses_the_one_global_json_pins`.
  Fails against release.yml or a single ci.yml step installing another SDK, a `.yaml` workflow
  installing another SDK, a setup step that names no version, the build or runtime image on 8.0 (written
  with `--platform` or without), the target `net8.0`, and `global.json` pinning 8.0.
- `ToolchainContractTests.Every_project_builds_for_the_target_and_language_the_build_props_set`. Fails
  against a project file setting `TargetFramework` or `LangVersion`, and `LangVersion` moved off 12.
- `ToolchainContractTests.The_notices_name_the_image_the_gateway_runs_on`. Fails against the runtime
  image on 8.0, the runtime image alone moved to Ubuntu 26.04, and notices naming the floating `10.0` tag.
- `LicenseContractTests.The_image_claims_no_version_of_its_base`, and the image jobs' label checks, which
  fail against the image as it was built before decision 3.
- `MetadataPersistenceFailureTests`:
  - `The_queue_is_flushed_even_when_the_writer_is_stopped_before_it_started` fails without the flush in
    `StopAsync`;
  - `Stopping_a_writer_that_never_started_waits_no_longer_than_the_host_allows`, whose store blocks
    the thread that asks as SQLite's busy wait does, fails with that flush unbounded or on the stopping
    thread, and without it;
  - `A_stop_that_times_out_while_the_loop_is_writing_starts_no_second_reader` fails when either the
    loop or `StopAsync` stops claiming the queue;
  - the three tests of the writer's loop wait until the loop has attempted every batch before they stop
    it, so they fail when the loop stops writing, and `The_writer_keeps_draining_after_a_failure`
    queues one batch and one record, so that a second batch follows the refused one.
- `RetentionSweepTests.The_startup_sweep_has_finished_when_the_service_has_started`. Fails with the first
  sweep back at the top of `ExecuteAsync`. `A_deployment_that_retains_nothing_starts_without_asking_for_a_store`
  fails with that sweep's guard removed.
- `MigrationLockTests.A_lock_left_by_a_killed_gateway_fails_startup_and_names_the_lock`, with
  `A_store_nothing_has_locked_starts_without_waiting_for_the_bound` as its control. The first fails with
  the migration unbounded. `A_start_stopped_while_it_waits_for_the_lock_is_not_reported_as_the_lock`
  fails when any cancellation is reported as the lock.
- `NullSettingValidatorTests`. Fails with the validator unregistered, and, for a runtime's settings,
  with lists not walked.
- `AgentSpliceOptionsValidatorTests`: the retention windows and the sweep interval at, below, and above
  their bounds. Fails with either bound back to "greater than zero".
- `SelfContainedPackageContractTests.Every_statement_of_the_Linux_packages_glibc_floor_is_the_one_its_smoke_test_enforces`.
  Fails against the release notes, the installation guide, or the smoke test stating another floor.
- The package smoke test refuses a static web assets manifest, and fails with `StaticWebAssetsEnabled`
  removed. Its glibc check, run in an Ubuntu 24.04 container against a Linux package, passes at 2.34 and
  fails at 2.33 and without `readelf`.
- The full suites, both packages' smoke tests, and the image jobs, in CI on Windows and Linux.

Checked by hand on Windows 11, and not a test:

- The win-x64 package that `package.ps1` built with SDK 10.0.204 passes every check of
  `smoke-package.ps1`, the new license and the absent static web assets manifest included.
- That package, started against a store holding a leftover lock row, exited after 31 seconds with a
  non-zero code, never listening, and an error naming the store and the lock. With the row deleted as
  the error says, it started.
- A streamed completion through that package, against a fake runtime on loopback sending one event a
  second, reached the client event by event, each within a few milliseconds of being sent, and its
  headers with the first event, as ADR 0019 describes.
- The image built from this Dockerfile:
  - becomes healthy and answers `/health/live`;
  - refuses `/api/v1` without its token and serves the dashboard;
  - runs as `app` on Ubuntu 24.04.5 with runtime 10.0.13;
  - applies the initial migration to a new store;
  - carries the labels the Dockerfile sets, its version empty, which both image jobs' label checks
    accept, and which they refuse in the image built before decision 3.
- A trimmed publish still raises IL2026 and IL2104 trim warnings, and IL2070 for
  `NullSettingValidator`'s reflection, so the packages stay untrimmed.
- In review, the Linux package started on Ubuntu 22.04, Debian 12, and AlmaLinux 9, and aborted on
  Ubuntu 18.04 and 20.04, Debian 10 and 11, and AlmaLinux 8, each lacking a glibc version
  `libe_sqlite3.so` asks for; with persistence off it also ran on Ubuntu 18.04.
- During the readiness review's trial, a store created by the Windows package CI built for `6e58aa1`
  (Entity Framework Core 8.0.11) opened on .NET 10, with nothing applied.

## Alternatives considered

- **Release the alpha on .NET 8 and move afterwards.** The tag waits for other Stage 1D work anyway.
  The alpha would ship a runtime with days of support left, the first users' stores would be created by
  Entity Framework Core 8, and the request-path corrections still to come would be validated on Kestrel 8
  and then again on 10.
- **.NET 9.** Its support ends with .NET 8's.
- **.NET 11.** A release candidate today, and a short-term support release, which Microsoft supports for
  24 months, so its support would end about when .NET 10's does.
- **Targeting both 8.0 and 10.0.** Rejected in decision 1.
- **C# 14 together with the runtime.** Rejected in decision 2.
- **The floating `10.0` tags.** They name the same images today, but the notices would then describe
  whatever the tags name next.
- **Ubuntu 26.04 (`10.0-resolute`).** Microsoft publishes .NET 10 on it too. The floating tags name
  24.04, whose standard support runs to May 2029, past .NET 10's own end, and the runners that build and
  run the image are 24.04. Moving the operating-system layer again is its own decision.
- **A version label from a build argument.** The label would need the version before the image is
  built, and the image job reads it from the gateway it built.
- **An `IsEnabled` guard around each call CA1873 flagged.** It answers a performance question nobody
  has measured, which is the reason CA1848 is waived.
- **Keeping the flush only in `ExecuteAsync` and waiting in the test harness.** A delay in the harness
  would hide the defect in the tests and leave it in the product.
- **Deleting a leftover migration lock at startup.** One gateway per store is the supported deployment,
  so a lock found at startup is almost certainly stale. But the gateway cannot know that no other process
  is migrating the same file, and the lock exists for that case. Failing with the fix in the message
  keeps the decision with the operator.
- **Enforcing the retention window on every read.** It would also close the gap between sweeps, which
  existed on .NET 8, but it changes what the administrative API returns, which is not what a runtime
  move is for.
- **Reading a null setting as its default.** The binder writes the null before anything can tell it
  apart from a value someone meant, and failing is what .NET 8 did.
- **Auditing transitive packages.** More is known, at the cost of builds that fail for users when an
  advisory appears. Where that failure belongs, CI or every build, is its own decision.
