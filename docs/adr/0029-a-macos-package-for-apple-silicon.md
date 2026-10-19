# ADR 0029 — A macOS package for Apple silicon

- Status: Accepted
- Date: 2026-10-19
- Related: [ADR 0017](0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md) (the Windows package),
  [ADR 0021](0021-stage-1d-a-linux-package-that-needs-no-dotnet-installation.md) (the Linux package this
  one mirrors; decisions 4 and 5 amended here), [ADR 0018](0018-stage-1d-tagged-releases-as-reviewed-drafts.md)
  (what a release publishes; decisions 2 and 5 extended here), [ADR 0020](0020-stage-1d-the-project-is-licensed-under-apache-2.md)
  (the license and notices every artifact carries), [ADR 0027](0027-stage-1d-what-a-local-gateway-exposes-is-closed-or-said.md)
  (runner labels GitHub moves)

## Context

On a Mac, AgentSplice ran only in Docker — the image is `linux/amd64`, which Docker Desktop emulates on
Apple silicon — or from source, with the .NET 10 SDK, Node, and pnpm. Many people who run models
locally do it on Apple silicon. Launch decision D3, taken on 2026-10-19, approved a native package for
it, built and smoke-tested on a macOS runner, with no developer-identity signing and no notarization,
and on the maintainer's word that nothing waits for it: not the launch, not CI, and not a release.

Nothing in the gateway is specific to a platform, as ADR 0021 found for Linux: the content-root switch,
the store's path, and the invariant globalization hold on macOS as they are. What differs is outside the
code:

- **Apple silicon runs no unsigned code.** Since .NET 10, the SDK's host model signs a single-file
  macOS executable ad hoc, in managed code, on whatever machine publishes it (`Bundler` calls
  `MachObjectFile.AdHocSignFile` when `_EnableMacOSCodeSign` is on, the default for `osx` runtime
  identifiers). SQLite's library, from SQLitePCLRaw 2.1.13, arrives signed ad hoc by Apple's linker. An
  ad-hoc signature proves a file is intact, and names nobody.
- **Gatekeeper refuses a quarantined program that Apple has not notarized.** A browser marks what it
  downloads with `com.apple.quarantine`, and Finder copies an archive's mark onto everything it
  extracts. `curl` sets no mark.
- **macOS's tools are not the Linux ones.** Its `tar` is bsdtar, which takes none of the options the
  package script gives GNU tar; its Bash is 3.2; `shasum` stands in for `sha256sum`, and `lsof` for `ss`.
- **The floor.** Microsoft supports .NET 10 on macOS 15, 26, and 27. The runtime's native code asks for
  macOS 12.0 and SQLite's library for 11.0. GitHub's `macos-15` label is arm64 on macOS 15;
  `macos-latest` already moved to macOS 26.

## Decisions

### 1. A self-contained, single-file osx-arm64 package

`src/AgentSplice.Api/Properties/PublishProfiles/osx-arm64.pubxml` is the Windows and Linux profiles'
twin, for the reasons ADR 0017 gives: self-contained and single-file; not trimmed, not ReadyToRun, not
compressed; native libraries beside the executable (`libe_sqlite3.dylib`); no IIS assets; no
development settings; and the runtime switch that makes the executable's directory the content root.
It is for Apple silicon only.

The archive is `agentsplice-<version>-osx-arm64.tar.gz`, written as the Linux one is: one top-level
folder, owner and group 0, folders and the executable `rwxr-xr-x`, every other file `rw-r--r--`, and
entries sorted. The package script writes it with GNU tar, which is `tar` on Linux and `gtar` on macOS,
and checks that it is GNU tar; a tar.gz is refused on Windows only, where no archive can record the
execute bit. The package's README says it runs on macOS 15 or later, to keep the folder at mode 700 as
on Linux, and how to clear the quarantine mark.

### 2. Signed ad hoc and not notarized; the guide clears the quarantine mark

Nothing signs the package with a developer identity and Apple does not notarize it: D3 keeps both out
of the launch, and both need an Apple Developer account. The ad-hoc signature the SDK writes is what
lets Apple silicon run it at all.

So a browser's download of it does not open. The installation guide, the package's README, and the
release notes give one command, run on the extracted folder once the archive has matched its line in
`SHA256SUMS`:

```bash
xattr -dr com.apple.quarantine <folder>
```

`-r` clears every file and folder, SQLite's library included, which a mark left behind would keep from
loading. The digest check comes first because clearing the mark skips the check macOS makes the first
time it opens a quarantined file. A package downloaded with `curl`, as the README's commands do, carries
no mark and needs nothing.

### 3. Built and smoke-tested on macOS 15

A `package-macos` CI job runs the package script and the shared smoke test on `macos-15`, and uploads
`agentsplice-osx-arm64`, as `package-linux` does on Linux. macOS 15 is the oldest version Microsoft
supports .NET 10 on, and the floor the documents state, so a package that ran there runs on every
version they name; the label is pinned, as Linux's are (ADR 0027), because a label GitHub moves would
move that proof with it. The job restores no dependency cache.

The smoke test holds the macOS package to every check the Linux one meets, from an empty folder
elsewhere, and adds what only a Mac shows:

- the installation guide's own `shasum` command passes, and fails for a file `SHA256SUMS` does not list;
- the archive's listing, read with `gtar`, has the Linux package's owner and modes and no `._`
  (AppleDouble) entries; the archive is extracted with the system's `tar`, as the guide does;
- the quarantine command succeeds silently on a folder with no mark; with every file and folder marked,
  as Finder leaves a browser's download, it clears them all; whether bsdtar carries an archive's mark
  onto what it extracts is printed, not required;
- every native file, found by its Mach-O magic, is arm64 only, passes `codesign --verify --strict`, is
  signed ad hoc, and asks for no macOS later than 15.0;
- the listener is found with `lsof`, and the shared gateway smoke test runs under `/bin/bash`, which is
  Bash 3.2.

### 4. The package gates nothing

The job has `continue-on-error: true`: when it fails, the CI run still passes and a release still goes
out. Nothing else in it can be skipped or allowed to fail, and the upload is its last step, so its
artifact, and the artifact ID it reports, exist only if every check passed. A failed job with
`continue-on-error` reads as a success to the jobs that need it, so nothing decides by its result: what
decides is whether it reported an ID. The job has a 30-minute timeout, because `release-assets` waits
for it.

### 5. A release carries the macOS archive exactly when its job uploaded one

`release-assets` needs the macOS job. It downloads that job's artifact by ID only when the job reported
one — with no ID, `download-artifact` would take every artifact of the run — and expects the macOS
archive in the release's assets exactly then. Otherwise it assembles the release without it and warns.
The Windows and Linux packages and the image stay required, as before.

The release workflow's `publish` job accepts exactly the three required archives, or those and the
macOS one, and nothing else. The draft's notes then either describe the macOS package — Apple silicon,
macOS 15 or later, signed ad hoc, not notarized, and the quarantine command — or say that this release
has none because its job did not pass. The person who publishes the draft decides with that in front of
them.

### 6. The guide says what differs

`docs/INSTALLATION.md` gains a macOS package section: the archive checked by its own line in
`SHA256SUMS` with `shasum -a 256 --check --strict`; extracted into a folder at mode 700 under the home
folder, outside Desktop, Documents, and Downloads; what to do when macOS refuses to open it, in the order
above; and the token, configuration, data, upgrade, and uninstall, which are the Linux package's. An
Intel Mac uses the image, which runs there natively, or the source.

## Consequences

- An Apple silicon Mac needs neither Docker nor .NET. The package carries a runtime that only a new
  AgentSplice release patches, like the others, and Microsoft supports .NET 10 until November 14, 2028.
- A release can come without the macOS package. Its draft says so, and the README and the guide, which
  name the build's version, then offer a download that release does not have; the release runbook
  (`docs/DEVELOPMENT.md`) has the maintainer check the macOS job before tagging, and say what to do with
  a draft that lacks it.
- CI gains a macOS job that never fails a run. Its failures show in its own log and as the warning
  `release-assets` writes on the run's summary, not as a failed run. macOS runners can queue longer than
  Linux ones, and `release-assets` waits for the job to finish or time out.
- Nothing signs the package with an identity. The digests detect corruption in transfer, and the
  ad-hoc signature only that a file changed after it was signed; who built it rests, as for the other
  packages, on the release and its attestation (`docs/THREAT_MODEL.md`).
- No runtime behaviour changes. No request-path code, log, metric, or configuration validation is
  touched, so those parts of the definition of done do not apply.
- The package's archive was also written on Linux, in a .NET 10 SDK container, to check its listing and
  its native files before CI; the smoke test runs only on GitHub's macOS runner, since no Mac was at
  hand. Neither is a performance claim.

## Tests proving these decisions

- `SelfContainedPackageContractTests`: the self-contained profiles are exactly `linux-x64`, `osx-arm64`,
  and `win-x64`, each held to every setting above; the package script accepts exactly those.
- `ReleaseWorkflowContractTests`:
  - every platform the package script builds has a CI job that builds it, smoke-tests it, uploads
    `agentsplice-<platform>`, and reports its ID; the Windows and Linux jobs gate, and the macOS job may
    fail, has a timeout, can skip no step, and uploads last;
  - `release-assets` needs every package job and the image's, can skip no step but the download of the
    macOS artifact, whose condition is exactly that its job reported an ID, and its verification is told
    that ID and names the macOS archive;
  - the release's verification names every platform's archive, and its notes say when a release has no
    macOS package;
  - the floor: the smoke test's `$macosFloor`, the "macOS N or later" the guide, the package's README,
    and the release notes state — and the README, once it offers the package — and the macOS job's
    `macos-N` runner are one version;
  - every document that offers the macOS package gives `xattr -dr com.apple.quarantine`, and the smoke
    test runs it;
  - a package file name for `osx-arm64` in the README or the guide names the build's version.
- The CI `package-macos` job: the smoke test against the real package on macOS 15.

## Alternatives considered

- **A gating job, like the other packages.** The maintainer chose otherwise for D3: a macOS runner
  outage or a Mac-only defect would hold back every release, Windows and Linux included.
- **Building on Linux and smoke-testing on macOS.** GNU tar is native there and the build can be
  reproduced on a workstation without a Mac, but it splits one platform across two jobs and an artifact
  handoff, and the smoke test still needs a Mac.
- **osx-x64.** Apple no longer sells Intel Macs, and the image runs natively on them.
- **Developer ID signing and notarization.** They need an Apple Developer account and a signing
  identity in CI; D3 keeps them out of the launch.
- **A `.pkg`, a `.app`, or a Homebrew tap.** Each is an installer or a channel to maintain before the
  alpha has users asking for one.
- **Telling users to allow each file in Privacy & Security.** It works one file at a time, and SQLite's
  library is a second refusal after the first; the guide mentions it as the alternative.
