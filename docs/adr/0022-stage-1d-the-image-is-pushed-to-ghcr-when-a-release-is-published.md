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
main's copy. Either way it refuses a release that is not published, that is not immutable (decision 5),
or that GitHub Actions did not create or whose image asset it did not upload. Every workflow's token
acts as `github-actions[bot]`, so that check tells a workflow's release from a person's upload, not one
workflow from another.

No other job of any workflow can write to the registry, so no workflow on `main` reaches it when a tag is
pushed, and nothing is public before a person has reviewed the draft (ADR 0018 decision 3). A tag runs
the workflows of the commit it names, so that holds for a tag on a reviewed commit on `main`; anyone with
write access to the repository could push a commit whose workflow asks for `packages: write`, and that
access stays the boundary.

### 3. It pushes exactly the reviewed archive, and never moves a tag

The `push` job downloads the image asset and `SHA256SUMS` from the release, checks the archive against
its line there and against the digest GitHub recorded when the release workflow uploaded it, and reads
the archive's `manifest.json` before loading anything: exactly one image, named exactly as this
release's, and its config digest. It pushes only the version tag, never `latest`. If the registry
already has that tag, it must name the same config, or the job fails rather than move it. It pushes only
on one of two answers to that lookup — the tag is not there, or the token cannot read the package, which
then either does not exist yet or cannot be written by it either — and on any other failure it retries,
then stops.

Afterwards it asks the registry for the digest the tag names, fetches the manifest by that digest, and
checks that its bytes hash to it, that it is a single image manifest, and that its config is the
archive's: that digest is the one the notes name, so it is the manifest checked even if the tag moved in
between. Last, it checks that the image can be fetched by its digest anonymously, and the `notes` job
writes the digest into the release notes, editing them only when they do not already end with it.

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

The `push` job refuses a release that is not immutable, so the archive the pushed image must match can no
longer be replaced. A release published while the setting was off is deleted, with its tag, and released
again.

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
  repository, and the release notes name the digest to run it by.
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
  - every workflow declares its permissions, and no job of any workflow but `push` can write packages or
    runs a registry command;
  - its jobs are exactly `push` and `notes`, with exactly `packages: write` and `contents: write`, and
    the workflow's default is read;
  - no job declares an action, a container, or services, and no step uses an action, installs or
    downloads anything, or builds, runs, pulls, or tags an image;
  - the `push` job refuses a release that is not published, not immutable, or not made by GitHub
    Actions, and verifies the archive against `SHA256SUMS` and GitHub's record of it and reads its
    manifest before it loads it;
  - it pushes once, only on the lookup's two answers, refuses to move a tag, stops on any other failure,
    and names the digest whose manifest it fetched and hashed;
  - no `run:` in any workflow contains an expression, and no value in any workflow names `latest`;
  - the jobs that build, load, and push the image run on `ubuntu-24.04`, and the `image` job stops on the
    containerd store before it builds;
  - the `image` job builds once, with `docker build --provenance=false --sbom=false`, starts with
    `--no-build`, refuses Compose labels, and saves the running container's image after the smoke test;
  - the Compose service names an image no registry namespace can claim, and builds it rather than
    pulling;
  - the bash and the PowerShell `docker run` in the installation guide are, word for word, the one CI
    runs but for the image; every port a document's `docker` command or Compose publishes is on loopback;
    and every image name a document gives is this repository's.
- `ImagePublicationContractTests` also holds the release to the image: `release-assets` needs
  `image-archive` and expects the image's archive, and so does the release's `publish` job, and every
  step that makes, loads, or pushes the archive spells its name and the image's the same way.
- `ReleaseWorkflowContractTests` — the image artifact is one `release-assets` must take by its ID, every
  job `release-assets` needs can be neither switched off nor allowed to fail, and the release's `publish`
  job holds `contents: write` and nothing more.
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
