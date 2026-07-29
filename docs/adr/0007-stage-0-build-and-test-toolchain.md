# ADR 0007 — Stage 0 build and test toolchain

Status: accepted
Date: 2026-07-29
Supersedes: none
Related: ADR 0001 (modular monolith), ADR 0002 (System.Text.Json)

## Context

Stage 0 bootstraps the solution. Before any request-path code exists, the repository needs decisions
about SDK pinning, dependency versioning, analyzer strictness, formatting enforcement, how module
boundaries are enforced, and where shared test infrastructure lives.

These are cheap to decide once and expensive to change after eight projects depend on them.

## Decisions

### SDK pinned by feature band

`global.json` pins `8.0.100` with `rollForward: latestFeature`. Several .NET SDKs are commonly
installed side by side, including .NET 10. Without a pin, a local build and a CI build can use
different compilers for the same `net8.0` target, which turns "works on my machine" into a real
class of defect. `latestFeature` still accepts any installed 8.0.x, so a patch-level difference
between a developer machine and a CI runner does not break the build.

### Central package management

`Directory.Packages.props` holds every version; project files reference packages without a version.
A trace-and-conformance product compares behaviour across runs, and a version that silently differs
between two projects in the same solution undermines that. Central management also makes the
production/test dependency split visible in one file.

### Warnings are errors, code style is not

`TreatWarningsAsErrors` plus `AnalysisLevel=latest-recommended` makes the "no new compiler warnings"
clause in the definition of done a build property rather than a review obligation.

`EnforceCodeStyleInBuild` stays off, and formatting is verified separately in CI by
`dotnet format whitespace` in verify mode. Formatting drift and functional defects deserve different
failure modes: a misplaced brace should not block a build that is otherwise correct, and a
correctness warning should not be dismissible as a style nit.

`.gitattributes` normalises line endings to LF in every working tree, because the formatting check
runs on both Windows and Linux and a platform-dependent checkout would fail on Windows only.

Two analyzer rules are switched off with a documented reason in `.editorconfig`:

- `CA2227` (collection properties should be read only) — configuration option classes need settable
  collection properties for `Microsoft.Extensions.Configuration` to bind list sections.
- `CA1848` (use `LoggerMessage` delegates) — adopted where a hot path is demonstrated by the
  performance suite, not speculatively across the codebase.

### xUnit, NetArchTest, no fluent assertion library

xUnit is already named in the specification. `NetArchTest.Rules` enforces the dependency rules in
`docs/ARCHITECTURE.md`; the alternative is review, which holds until the first time a provider type
is convenient to reach for from `Application`.

No fluent assertion library is used. Assertions here are mostly about presence, absence, and
provenance, which plain xUnit expresses adequately, and FluentAssertions v8 changed to a commercial
licence — a dependency worth not acquiring for a public repository.

### Boundary enforcement uses assembly markers

Every production project exposes a sealed `AssemblyMarker` with a private constructor. Architecture
tests need a type reference to load an assembly deterministically, and two Stage 0 projects
(`Protocols.OpenAI`, `Providers.LmStudio`) are intentionally empty until Stage 1A. Loading by
assembly name would silently pass when an assembly is absent.

### Shared test infrastructure lives in `tests/AgentSplice.TestSupport`

`docs/ARCHITECTURE.md` did not list this project. The deterministic fake upstream is required by the
contract, integration, and later performance and conformance suites, so it cannot live inside one of
them. It references no production project, so the fake upstream behaves like a third-party runtime
rather than a mirror of the gateway's own types.

### The fake upstream is a real Kestrel listener

Not an in-memory handler. Streaming preservation, cancellation propagation, and timeout phases are
properties of the transport. An in-memory handler cannot demonstrate that a client disconnect reaches
the runtime, nor that events are flushed rather than buffered, and those are exactly the Stage 1
claims that need evidence.

### Loopback binding is a fallback, not a settings-file value

`docs/SECURITY.md` requires listening on loopback unless explicitly configured otherwise. That default
is applied in `Program.cs` when no binding is configured, not declared as a `Urls` value in
`appsettings.json`.

The distinction is not cosmetic. `WebApplicationBuilder` layers `appsettings.json` *over* the host
configuration that carries `ASPNETCORE_URLS`, so a `Urls` value in the settings file silently wins over
the environment variable. Expressed that way, the container bound to `127.0.0.1` inside itself and the
published port mapping could not reach it; the only symptom was a refused connection from outside.

`LoopbackBindingDefault` therefore applies the default only when `urls`, `HTTP_PORTS`, and
`HTTPS_PORTS` are all absent. A bare `dotnet run` stays on loopback; a container binds its own
interface and the operator maps the port to loopback on the host.

### The image context excludes build output

`.dockerignore` excludes `bin/` and `obj/`. Without it, host build output is copied into the image and
`dotnet publish --no-restore` reads a `project.assets.json` full of Windows paths. The resulting
failure is a NuGet path-resolution stack trace that never mentions the cause.

### The Stage 0 host exposes no HTTP surface

`AgentSplice.Api` boots, registers `TimeProvider`, and validates configuration. It serves nothing.
A placeholder response would let a client mistake an unimplemented gateway for a working one, which
is the same category of error as the rule that an HTTP 200 is never proof of compatibility.

## Consequences

- A developer with only .NET 10 installed must install an 8.0 SDK. This is stated in
  `docs/DEVELOPMENT.md`.
- Adding a package requires editing `Directory.Packages.props`, which is intentional friction.
- `AgentSplice.PerformanceTests` is deferred to Stage 1B, when gateway overhead exists to measure.
  Creating it now would add an empty project that asserts nothing.
- Turning an analyzer rule off requires a documented reason in `.editorconfig`, keeping the
  suppression list reviewable.

## Alternatives considered

**No SDK pin.** Rejected: reproducibility across machines is worth one file.

**`EnforceCodeStyleInBuild=true` with warnings as errors.** Rejected: it makes every IDE style
preference a build break, which pressures developers toward blanket suppressions.

**Fake upstream inside `AgentSplice.IntegrationTests`.** Rejected: the contract suite and later
performance and conformance suites need it, and a test project referencing another test project for
its fixtures is worse than a purpose-named support project.

**A single test project.** Rejected: `docs/TESTING.md` distinguishes unit, contract, integration, and
architecture suites because they have different runtimes and different failure meanings. Merging them
loses the ability to run the fast suites alone.
