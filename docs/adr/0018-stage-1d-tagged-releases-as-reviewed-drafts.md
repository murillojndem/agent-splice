# ADR 0018 — Stage 1D: tagged releases as reviewed drafts

- Status: Accepted, decision 6 refined by [ADR 0020](0020-stage-1d-the-project-is-licensed-under-apache-2.md),
  decisions 2 and 5 extended by [ADR 0021](0021-stage-1d-a-linux-package-that-needs-no-dotnet-installation.md) (both packages, assembled in CI and taken by artifact ID; one `SHA256SUMS`, checked by the line for the file downloaded)
- Date: 2026-10-15
- Related: [ADR 0007](0007-stage-0-build-and-test-toolchain.md) (Windows and Linux CI),
  [ADR 0016](0016-stage-1d-the-image-reports-its-health.md) (the image's health check),
  [ADR 0017](0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md) (the Windows package)

## Context

The Windows package exists to be installed without the .NET SDK, which means downloading it: a user
without the SDK cannot build it. CI builds it on every change, but a workflow artifact needs a GitHub
sign-in and expires. The roadmap lists a release workflow for Stage 1D, and `docs/SECURITY.md` asks
for releases whose integrity can be checked.

A release is the most public thing this repository does, and the hardest to take back. Anything that
can run with permission to create one can also replace what users download.

## Decisions

### 1. A tag that names the build's version starts a release, and nothing else does

`.github/workflows/release.yml` runs on `push` of a `v*` tag only. Its first job checks that the tag
is exactly `v` followed by the version the build stamps — `VersionPrefix` and `VersionSuffix` in
`Directory.Build.props` — so the release's name, the package's file name, and what `/api/v1/system`
reports are one value. A tag that disagrees stops the release before anything is built. A contract
test keeps the dashboard's `package.json` on the same version.

### 2. The release is the CI run, not a second build

The second job calls `ci.yml` as a reusable workflow against the tagged commit. That is the whole
suite: both platforms' tests, the dashboard, the image, and the Windows package, built and
smoke-tested. The package the release carries is the `agentsplice-win-x64` artifact that very run
uploaded. Nothing is rebuilt for publishing, so what is published is what was tested. The release has
its own concurrency group, because a reusable workflow that waits on its caller's group never starts.

> **Extended by ADR 0021.** The release carries the `agentsplice-release` artifact that CI's
> `release-assets` job assembles from both packages, taken by the artifact ID CI reports, and creates
> the draft from the files its `SHA256SUMS` lists.

### 3. Drafts, published by a person

The release is created with `gh release create --draft --prerelease --verify-tag`. Nobody else sees
it until a maintainer has looked at the assets and notes and published it. Every release is a
pre-release while the product is an alpha.

### 4. One job can write, and it installs nothing

The workflow's default permission is `contents: read`, and only the final job has `contents: write`.
That job checks nothing out and runs no restore, install, or build. It downloads the artifact,
verifies it with `sha256sum --check --strict SHA256SUMS`, and creates the draft. A dependency's install
script therefore never runs with the token in reach.

### 5. Digests, not yet signatures

The release carries `SHA256SUMS` beside the archive, and the installation guide tells users to check
it with `Get-FileHash`. GitHub also records each asset's digest. The package is not Authenticode
signed and no build provenance is attested: signing needs a certificate or a signing service, and
attestation is a separate decision. Both are recorded as residuals rather than implied.

> **Extended by ADR 0021.** One `SHA256SUMS` lists every asset, and the installation guide checks a
> download by its own line: `Get-FileHash` on Windows, `sha256sum --check` on Linux.

### 6. A license gates the release

The version job also refuses to release without a `LICENSE` file. `LICENSE-CHOICE.md` recommends a
license but leaves the decision to the maintainer and asks for the license text before anything is
published. A release would otherwise publish code nobody may use.

> **Refined by ADR 0020.** The maintainer chose Apache-2.0, `LICENSE-CHOICE.md` was removed, and the gate
> now requires `LICENSE`, `NOTICE`, and `THIRD-PARTY-NOTICES.md`.

### 7. The image is not pushed

The roadmap's "Docker image, published" stays open. A registry adds credentials, tags, and
architecture decisions that the Windows package does not need. A release's image is built from its tag
with `docker compose up --build --wait`, which the release notes say.

## Consequences

- Publishing is one push of a tag and one review, and the tag must be right before anything is built.
- The first release cannot be cut until a `LICENSE` file exists.
- Retrying a release means deleting the draft and the tag, because `gh release create` refuses a tag
  that already has a release.
- The workflow itself runs only when a tag is pushed. Its first real run is the first tag, and its
  output is a draft that can be deleted.

## Tests proving these decisions

- `ReleaseWorkflowContractTests`:
  - a release is created only as a draft pre-release of an existing tag;
  - only the publishing job may write, and it checks nothing out and installs nothing;
  - the release publishes the artifact `ci.yml`'s package job uploads, and `ci.yml` is callable;
  - only a `v*` tag starts a release, and it is checked against the build's version;
  - the dashboard and the gateway carry one version.

  Each fails against its reintroduced defect: no `--draft`, write granted at the top level, a checkout
  in the publishing job, and a renamed artifact.

## Alternatives considered

- **Publishing directly, without a draft.** A mistake would be public the moment the job finished, and
  the person who pushed the tag would review it after users could already download it.
- **Building the package again in the release job.** What was published would not be what CI tested,
  and the job holding the write token would run `pnpm install` and `dotnet restore`.
- **A manually triggered workflow taking a version as input.** The version would be typed rather than
  derived, and could disagree with the build and the tag.
- **Signing with a self-signed certificate.** It would satisfy nothing that checks signatures, and it
  would read as more assurance than it gives.
- **Pushing the image to GitHub Container Registry in the same workflow.** Rejected in decision 7.
