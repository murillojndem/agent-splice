# ADR 0021 — Stage 1D: a Linux package that needs no .NET installation

- Status: Accepted, decisions 2, 4, and 5 amended by [ADR 0029](0029-a-macos-package-for-apple-silicon.md)
  (a macOS package, written with GNU tar on Linux or macOS, built by a job that gates nothing, and in a
  release exactly when that job uploaded it)
- Date: 2026-10-16
- Related: [ADR 0017](0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md) (the Windows package
  this one mirrors), [ADR 0018](0018-stage-1d-tagged-releases-as-reviewed-drafts.md) (what a release
  publishes; decisions 2 and 5 extended here), [ADR 0020](0020-stage-1d-the-project-is-licensed-under-apache-2.md)
  (the license and notices every artifact carries)

## Context

On Linux the direct-process path was building from source: the .NET 8 SDK for `dotnet run`, or the
ASP.NET Core 8 runtime for a framework-dependent build, and Node and pnpm for the dashboard either way.
The Windows package removed those prerequisites on Windows (ADR 0017), and the maintainer asked for a
Linux package beside it in every release.

Nothing in the gateway is Windows-specific. The content-root switch reads `AppContext.BaseDirectory`
with no path handling of its own, the store resolves a relative path against the content root on every
platform, and the project sets `InvariantGlobalization`, so a Linux build needs no ICU. What differs is
outside the code: the archive must keep the executable's permission bit, SQLite's native library is
`libe_sqlite3.so`, the listener is found with other tools, and two habits of Windows users do not hold
on Linux — environment variable names are case-sensitive, and `~` is something the shell expands, not
the gateway.

Adding a second package also meant a release with more than one artifact, and a review of the change
found what a release assembled from several artifacts must not trust: the name an artifact was uploaded
under, which any job of the same run can upload again, and a digest that came in the same artifact as
the file it describes.

## Decisions

### 1. A self-contained, single-file linux-x64 package

`src/AgentSplice.Api/Properties/PublishProfiles/linux-x64.pubxml` is the Windows profile's twin, for
the reasons ADR 0017 gives: self-contained and single-file; not trimmed, not ReadyToRun, not
compressed; native libraries beside the executable; no IIS assets; no development settings; and the
runtime switch that makes the executable's directory the content root. It targets glibc distributions;
Alpine, on musl, would need a `linux-musl-x64` build that is not made.

### 2. A tar.gz, built on Linux, with modes the script sets

The archive is `agentsplice-<version>-linux-x64.tar.gz` with one top-level folder, so that `tar`
restores the executable's execute bit wherever it is extracted; a zip records it only when written by
particular tools and is restored only by particular ones. Every file's mode is set before the archive
is written rather than kept, because NuGet extracts its packages' files with an execute bit and the
copies keep it: the executable and the folders are `rwxr-xr-x`, every other file `rw-r--r--`, whatever
the build machine's umask. Owner and group are written as 0 by number, so the archive names no account
of the machine that built it, and its entries are sorted. It is built on Linux only, where the published
executable carries its execute bit; the script refuses otherwise.

> **Amended by ADR 0029.** A tar.gz is refused on Windows only: the macOS package is written on macOS,
> with GNU tar, which is `gtar` there, and either package can be written on Linux or macOS.

The README in the archive says how to start it from its own folder or by its full path, to keep the
folder to its owner with `chmod 700`, and, on a machine other people use, to set the administrative
token. It links the installation guide at the release's own tag when the build is that tag's, and the
repository otherwise, so a package built from anything else never links to a tag that does not exist.

### 3. One package script and one smoke test for both packages

`package-windows.ps1` becomes `package.ps1 -RuntimeIdentifier win-x64|linux-x64`, and
`smoke-windows-package.ps1` becomes `smoke-package.ps1`, both requiring PowerShell 7.4. Every step is
shared — the version, the dashboard, the publish, the license and the runtime's notices, the README,
the digest — and each platform differs only where the platform does: the archive format, the README's
line endings and commands, the file names, the modes, and the listener check (`Get-NetTCPConnection` on
Windows, `ss` on Linux).

The Linux package is held to every check the Windows one is, from an empty folder elsewhere: what it
contains, the dashboard, `/api/v1` without a token, a refused request read back from the store, the
store beside the executable, nothing written to the working directory, a listener on `127.0.0.1` only,
and the shared gateway smoke test with a token. On Linux the smoke test also runs the installation
guide's own digest check, reads the archive's listing before extracting it — every entry owned by 0:0,
and exactly the modes above — and checks the execute bit once extracted. On both platforms the README's
line endings are checked, and the content-root check matches the whole log line, so a sibling folder
whose name starts with the package's cannot pass for it.

### 4. Built and smoke-tested on every change, from the lockfile alone

A `package-linux` CI job on `ubuntu-latest` runs both scripts and uploads `agentsplice-linux-x64`, as
`package-windows` does for Windows. Neither package job restores a dependency cache: a release
artifact's dependencies come from the lockfile and the registry, never from a store another run wrote.

> **Amended by ADR 0029.** A third job, `package-macos`, builds and smoke-tests the macOS package on
> `macos-15`. It is allowed to fail without failing the run, and nothing in it can be skipped.

### 5. A release is assembled on every change, from artifacts taken by their IDs

A `release-assets` CI job takes each package artifact by the ID its job reported, not by its name, so
an artifact another job of the run uploaded again under the same name — archive and digest together —
makes the download fail rather than pass its own digest check. It checks that each holds one archive
and its one-line `SHA256SUMS` and verifies it, combines the lines into one sorted `SHA256SUMS`, checks
that there is exactly one archive per platform of one version — the tag's, on a tag — and nothing else,
verifies again, and uploads the result as `agentsplice-release`. It uses no action but the artifacts'
own, checks nothing out, installs nothing, and no step of it can be skipped or allowed to fail.

The release workflow's `publish` job takes that artifact by the ID CI reports to it, verifies it, checks
that `SHA256SUMS` lists exactly this tag's archives and the folder holds nothing else, and creates the
draft from the files `SHA256SUMS` lists, so its assets and the digests it publishes cannot drift apart
(ADR 0018 decision 2). A defect in putting a release together therefore fails a pull request rather than
a tag.

> **Amended by ADR 0029.** "Exactly one archive per platform" holds for Windows and Linux. The macOS
> archive is in the release exactly when its job reported an artifact ID, which it does only after every
> check in it passed; `release-assets` downloads it only then, and warns when it is missing.

### 6. The guide says what differs

`docs/INSTALLATION.md` gains a Linux package section: checking the archive by its own line in
`SHA256SUMS` with `grep -F "  <archive>" SHA256SUMS | sha256sum --check --strict -`, extracting into a
folder set to mode 700, running it, and the token, configuration, and data, with the two habits above
called out. Mode 700 keeps the store's file to its owner, because SQLite creates it readable by whomever
the umask allows; it does not keep what the gateway serves, so the guide recommends the token wherever
other accounts exist. Until the first release, the guide names only the artifacts of CI runs for a push
to `main`: a run for a pull request builds code nobody has reviewed, and its artifacts pass the same
digest check.

## Consequences

- A Linux user needs neither Docker nor .NET. Like the Windows package, the Linux one carries a runtime
  that only a new AgentSplice release patches, and it is not signed. Microsoft supports .NET 8 until
  November 10, 2026; after that date neither package can receive runtime fixes while AgentSplice is
  on .NET 8.
- A release carries two archives and one `SHA256SUMS` listing both; a user checks the one they
  downloaded by its line, on Windows and on Linux.
- Every change builds both packages and assembles a release, which adds two jobs: `package-linux`, in
  parallel with the others, and `release-assets`, after both packages.
- The digests detect corruption in transfer, not a substitution by whoever writes an archive and its
  line together; taking artifacts by ID closes the substitution by another job of the run, and signing
  and attested provenance remain follow-ups.
- No runtime behaviour changes. No request-path code, log, metric, or configuration validation is
  touched, so those parts of the definition of done do not apply.
- The Linux package was built and smoke-tested in a Debian 12 container — the .NET 8 SDK image — on a
  Windows workstation, and is on the `ubuntu-latest` runner in CI; neither is a performance claim.

## Tests proving these decisions

- `SelfContainedPackageContractTests`, which replaces `WindowsPackageContractTests`:
  - the self-contained profiles are exactly the platforms a package is built for;
  - over both profiles: each is self-contained, single-file, untrimmed, uncompressed, not ReadyToRun,
    with native libraries beside the executable and no IIS assets, carries the content-root switch, and
    excludes the development settings;
  - the package script accepts exactly those platforms, its publish takes the profile named for the one
    it is given, and it names no profile by a literal;
  - nothing package-specific is in the project file or `Directory.Build.props`.
- `ReleaseWorkflowContractTests`:
  - every platform the package script builds has a CI job that builds it, smoke-tests it, uploads
    `agentsplice-<platform>`, reports the artifact's ID, restores no cache, and has no step that can be
    skipped or allowed to fail;
  - `release-assets` takes every `agentsplice-*` artifact by its job's reported ID and none by name,
    needs every job that uploads one, uses no action but the artifacts' own, installs nothing, verifies
    strictly, uploads `agentsplice-release` from `release/`, and reports its ID, which CI reports to
    the release;
  - the release takes that artifact by ID and nothing else, verifies it before it creates anything,
    checks every platform's archive, and creates the draft from what `SHA256SUMS` lists.
- The CI `package-linux` job — the guide's digest check, the archive's listing, and both runs of the
  smoke test against the real package on Linux.
- The CI `release-assets` job — the combined digests verify and the set is complete, on every change.

Each fails against its reintroduced defect: the Linux artifact renamed, its smoke test dropped, switched
off, or allowed to fail, or its job reporting no artifact ID; a cache restored in a package job; a
download missing from `release-assets`, one taken by name, a checkout, an action, or an install added
to it, a `needs` entry missing, its verification no longer strict, or its upload nested; CI reporting
another job's artifact to the release; the release taking its artifact by name, verifying after it
creates, checking only the Windows archive, or naming its assets by hand; the script accepting only
Windows or publishing with a literal profile; and the Linux profile deleted, trimmed, without the
switch, or shipping the development settings.

## Alternatives considered

- **A separate Linux script.** The two packages would drift: the license step, the runtime's notices,
  and the README are the same in both, and were written once.
- **A zip, as on Windows.** The execute bit survives only some writers and some extractors.
- **Keeping the modes the files arrive with.** NuGet's files carry an execute bit, so license texts and
  a shared library would ship executable, and the result would depend on the build machine's umask.
- **A framework-dependent tarball.** It needs the ASP.NET Core 8 runtime installed, which this package
  exists to remove; it remains documented under "From source".
- **`.deb`, `.rpm`, AppImage, Flatpak, or a systemd unit.** Each is an installer or a lifecycle to
  maintain before the alpha has users to ask for one.
- **linux-arm64 and linux-musl-x64.** Each is another build and another smoke test on another runner;
  they wait until someone needs them.
- **Assembling the release in the release workflow only.** It would run for the first time on the first
  tag.
- **Taking artifacts by name.** Any job of the run can upload an artifact again under the same name,
  with a digest to match.
- **Creating the store with mode 0600.** It would protect the file wherever the package is extracted,
  but it changes runtime behaviour, which this slice does not; it is a follow-up the threat model
  records.
