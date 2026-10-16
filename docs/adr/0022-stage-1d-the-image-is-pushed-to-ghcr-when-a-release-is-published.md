# ADR 0022 — Stage 1D: the image is pushed to GHCR when a release is published

- Status: Accepted
- Date: 2026-10-16
- Supersedes: decision 7 of [ADR 0018](0018-stage-1d-tagged-releases-as-reviewed-drafts.md), and extends
  its decisions 2 and 4
- Related: [ADR 0016](0016-stage-1d-the-image-reports-its-health.md) (the image's health check),
  [ADR 0020](0020-stage-1d-the-project-is-licensed-under-apache-2.md) (the labels and notices the image
  carries), [ADR 0021](0021-stage-1d-a-linux-package-that-needs-no-dotnet-installation.md) (the release
  assets CI assembles)

## Context

ADR 0018 left the roadmap's "Docker image published to a registry" open: a registry adds credentials,
tags, and architecture decisions the Windows package did not need, so a release's image was built from
its tag. The maintainer asked for the image on GitHub Container Registry, and chose when it becomes
public: when a person publishes the release, not when a tag is pushed.

Three facts about GHCR shaped the rest:

- A workflow that pushes with `GITHUB_TOKEN` from a public repository creates the package with the
  repository's visibility — public — and links it to the repository. GitHub's own pages disagree about
  the default, and the first push is the only way to see which one applies.
- A public package can never be made private again, and a public version can be deleted only while it
  has fewer than 5,000 downloads.
- A tag is mutable: anything that can push to the package can point `0.2.0-alpha` at another image. A
  digest is not.

And two about the image the CI job builds:

- Compose labels the images it builds with its project, service, and version. An image published with
  them would make every container run from it look like part of somebody's Compose project.
- On the containerd image store, which Docker 29 makes the default for new installations and
  `ubuntu-latest` moves to in November 2026, a build carries attestation manifests and an image's ID is
  its manifest's digest rather than its config's, so what is saved, loaded, and pushed is a different
  kind of thing than on the classic store.

## Decisions

### 1. The tested image travels as a release asset

The CI `image` job builds the image with `docker build --provenance=false --sbom=false` — not through
Compose — starts it with `docker compose up --no-build`, and runs every check it ran before. Then it
refuses an image that carries a Compose label, reads the version from the running gateway's
`/api/v1/system`, tags the image `ghcr.io/<owner>/<repository>:<version>`, and saves it as
`agentsplice-<version>-image-linux-amd64.tar.gz` with its `SHA256SUMS` line. The `release-assets` job
adds it to what a release publishes, so the draft a tag creates carries the image beside the packages,
covered by the same `SHA256SUMS`, and a maintainer reviews it with them.

A new `image-archive` job loads that archive on a runner that never built it, checks what the archive
says it holds — one image, under exactly that name — and its labels and platform, and starts it with
the `docker run` command `docs/INSTALLATION.md` gives, then runs the shared gateway smoke test against
it and checks that the store is on the volume. A contract test holds the command in the guide and the
command in CI to the same flags. `release-assets` takes the image artifact by its ID, like the packages
(ADR 0021), and only after `image-archive` has passed.

Compose's own image is renamed `agentsplice:dev`, with `pull_policy: build`. Its old name,
`agentsplice/agentsplice:dev`, belonged to a Docker Hub account anyone could register; once users have a
published image to pull, a squatted name there would be a way to run somebody else's image with their
token and their runtime's key. A name without a `/` is in Docker Hub's library namespace, where no
account can publish, and Compose now builds it rather than pulling it.

### 2. The registry is reached only when a person publishes the release

`.github/workflows/publish-image.yml` runs on `release: published` — a maintainer publishing a draft —
and on `workflow_dispatch` with a tag, because a `release` event runs the workflow file as it was at the
tag, so a fix to the workflow reaches an already-published release only through dispatch, which runs
main's copy. Either way it refuses a release that is not published, that the release workflow did not
create, or whose image asset the release workflow did not upload. Pushing a tag can therefore never
reach the registry, and nothing is public before a person has reviewed the draft (ADR 0018 decision 3).

### 3. It pushes exactly the reviewed archive, and never moves a tag

The `push` job downloads the image asset and `SHA256SUMS` from the release, checks the archive against
its line there and against the digest GitHub recorded when the release workflow uploaded it, and reads
the archive's `manifest.json` before loading anything: exactly one image, named exactly as this
release's, and its config digest. It pushes only the version tag, never `latest`. If the registry
already has that tag, it must name the same config, or the job fails rather than move it. Afterwards it
checks that the registry holds a single image manifest whose config is the archive's, and that the image
can be fetched by its digest anonymously.

### 4. Each job that writes, writes one kind of thing

ADR 0018 confined write access to one job. There are now three, each with one permission and none
running anything fetched from a package registry — no checkout, no install, no action at all in this
workflow:

- the release workflow's `publish` job holds `contents: write`, to create the draft;
- `publish-image.yml`'s `push` job holds `packages: write`, to push;
- its `notes` job holds `contents: write`, to write the registry's digest into the published release's
  notes, which stay editable on an immutable release.

Every value a step uses arrives through `env:`, and no `run:` contains an expression, so nothing a tag
name or an input carries can become part of a script.

### 5. Releases are immutable once published

Release immutability is enabled for the repository. Once a release is published, its assets and its tag
cannot change, and GitHub attests the release; `gh release verify` and `gh release verify-asset` check
it. A draft stays editable, so retrying a release before it is published still means deleting the draft
and the tag; after publication, a tag is never reused.

### 6. Pinned to the classic image store, amd64 only

The `image`, `image-archive`, and `push` jobs run on `ubuntu-24.04`, whose Docker uses the classic image
store, and the `image` job refuses to run on the containerd store. Moving off it is a decision to revisit
with the runner. The image is built for `linux/amd64` alone, the platform the runner builds and the
smoke test proves; on an Apple silicon Mac, Docker Desktop runs it under emulation.

### 7. The image's labels name what GitHub shows

The image's `org.opencontainers.image.source`, `.description`, `.licenses`, and `.title` labels (ADR 0020)
are what GHCR shows on the package page and how it links the package to the repository.

## Consequences

- `docker pull ghcr.io/murillojndem/agent-splice:<version>` installs a release with nothing from the
  repository, and the release notes name the digest to pull it by.
- The first publication creates the package; whether it is public and linked as the documentation says
  can only be seen then, and the release runbook checks it.
- A release carries the image twice — in the registry and as an archive — and the archive is for
  `docker load` on a machine without registry access.
- An image pushed under a tag stays public; withdrawing one means deleting its package version while
  that is still possible.
- CI saves, uploads, and loads the image on every change, which adds an artifact and a job.
- The image's operating-system layer is Debian's, under the licenses of its packages, some of them GPL;
  `THIRD-PARTY-NOTICES.md` says where their notices and sources are.
- No runtime behaviour changes.

## Tests proving these decisions

- `ImagePublicationContractTests`:
  - `publish-image.yml` runs only on a published release and on dispatch with a tag;
  - its jobs are exactly `push` and `notes`, with exactly `packages: write` and `contents: write`, and
    the workflow's default is read;
  - no step uses an action, and none builds, tags anew, or installs anything;
  - the `push` job verifies the archive against `SHA256SUMS` and reads its manifest before it loads it,
    and pushes once;
  - no `run:` in any workflow contains an expression, and none pushes or tags `latest`;
  - the jobs that build, load, and push the image run on `ubuntu-24.04`;
  - the `image` job builds with `docker build --provenance=false --sbom=false`, starts with
    `--no-build`, refuses Compose labels, and saves after the smoke test;
  - the Compose service names an image no registry namespace can claim, and builds it rather than
    pulling;
  - the `docker run` in the installation guide and the one CI runs use the same flags, and every port
    the guides publish is on loopback.
- `ImagePublicationContractTests` also holds the release to the image: `release-assets` needs
  `image-archive` and expects the image's archive, and so does the release's `publish` job.
- `ReleaseWorkflowContractTests` — the image artifact is one `release-assets` must take by its ID.
- The CI `image`, `image-archive`, and `release-assets` jobs — the image saved, loaded elsewhere, run as
  the guide runs it, and assembled into the release, on every change.

## Alternatives considered

- **Pushing when the tag is pushed.** Simpler, but the image would be public before anyone reviewed the
  draft, and a public package cannot be made private again.
- **`docker/build-push-action` with provenance attestations.** It would rebuild the image in the job that
  pushes, and its attestations make the image an index whose description GHCR does not show unless it
  is annotated there.
- **An OCI layout pushed with `skopeo copy --preserve-digests`.** It would make the digest known before
  the push, at the cost of a second image format to build and test.
- **Downloading the image artifact from the tag's workflow run.** It would tie publication to an
  artifact that expires and to finding the right run; the release asset is what was reviewed.
- **A floating `alpha` or `latest` tag.** Every pull would mean something different over time; the
  version tag and the digest say exactly what a user runs.
- **linux/arm64.** Another build and another smoke test on another runner; it waits until someone needs
  it.
