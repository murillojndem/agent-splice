# ADR 0020 — Stage 1D: the project is licensed under Apache-2.0

- Status: Accepted
- Date: 2026-10-16
- Related: [ADR 0017](0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md) (the package that
  bundles a runtime), [ADR 0018](0018-stage-1d-tagged-releases-as-reviewed-drafts.md) (decision 6: a
  license gates the release, which this ADR refines)

## Context

The repository was public and had no license, so nobody could lawfully use, change, or pass on what it
published. `LICENSE-CHOICE.md` recommended the Apache License 2.0 — permissive, with an explicit patent
grant, usual for infrastructure — but left the decision to the maintainer. ADR 0018 made the release
workflow refuse to run without a `LICENSE` file for that reason, so no release could be cut.

The note itself was part of the problem. GitHub's license detection reads every file at the root whose
name looks like a license, and it reported the repository's license as "Other" because of
`LICENSE-CHOICE.md`. A second file that disagrees with `LICENSE` would keep it there.

What the project distributes also passes on other people's code. The Windows package bundles the .NET
runtime and ASP.NET Core, Entity Framework Core, SQLite, OpenTelemetry, and the dashboard's React
libraries, and the image built from the repository carries the same managed dependencies. Their licenses
— MIT, Apache-2.0, public domain — all permit that, and the MIT and Apache licenses require the notices
to travel with the code.

The repository also had no working channel for vulnerability reports. `SECURITY.md` pointed at GitHub's
private vulnerability reporting "if enabled", and it was not. The two issue forms carried `about`, the
key of a Markdown template, where an issue form takes `description`, so GitHub offered neither.

## Decisions

### 1. Apache-2.0, word for word

The maintainer chose the Apache License 2.0. `LICENSE` is the text the Apache Software Foundation
publishes at <https://www.apache.org/licenses/LICENSE-2.0.txt>, byte for byte. A license with an edited
body is not the license it names, so a contract test pins the file's SHA-256.

`NOTICE` names the product and the copyright holder, `Copyright 2026 Murillo Jorge Nóbrega de Melo`.
`Directory.Build.props` carries the same line as `Copyright`, so the assemblies say it too.

### 2. One license file at the root

`LICENSE-CHOICE.md` is removed; its reasoning is this ADR's context. Exactly one file at the root has a
name GitHub's detection reads as a license, a copyright, or a patents statement, and there is no
`LICENSES` directory; a test fails otherwise.

### 3. What is distributed carries the license and the notices of what it bundles

`THIRD-PARTY-NOTICES.md` lists every component that is distributed, with its license and copyright, and
the notices those licenses require. Its sources are the package's own license files where it ships them
(the `Microsoft.Extensions.*` and `System.*` packages, OpenTelemetry, the npm packages), and otherwise the
nuspec's license expression and `<copyright>` (SQLitePCLRaw, OpenTelemetry's holder) or the upstream
repository's license (Entity Framework Core and Microsoft.Data.Sqlite, whose nuspec names a different
holder than their license does). OpenTelemetry's own notices for the code it adapted from .NET and from
gRPC for .NET are carried through. Vite, Rollup, and esbuild have a row too: they are build tools, but
the code they generate — Vite's module-preload polyfill and esbuild's helpers among it — is part of the
dashboard's bundle.

- **The package.** The package script copies `LICENSE`, `NOTICE`, and `THIRD-PARTY-NOTICES.md` beside
  the executable and refuses to build without them. The package is self-contained, so the script also
  copies, into `licenses/`, the .NET runtime pack's `LICENSE.TXT` and `THIRD-PARTY-NOTICES.TXT` and the
  ASP.NET Core runtime pack's `THIRD-PARTY-NOTICES.TXT`. ASP.NET Core 8's pack has no separate license
  file; ASP.NET Core is MIT under the same holder, whose notice `THIRD-PARTY-NOTICES.md` carries. The
  script asks the SDK which runtime packs the publish used, and from which directory, and fails if any of
  the three files is missing. The smoke test checks all six in the archive.
- **The image.** The runtime stage copies the three files into `/app`. Its base image already carries
  the .NET runtime's notices in `/usr/share/dotnet`.
- **The release.** The release workflow's first job refuses to run without the three files.

A contract test reads the gateway's restored dependency graph and the dashboard's production graph
from its lockfile, transitive packages included. It fails for any package that is neither in the Packages
column of the notices' Components table nor in its list of packages that do not ship. A name ending in
`.*` covers a family, such as `Microsoft.Extensions.*`. The transitive packages are the ones a list of
direct references would miss: SQLitePCLRaw, the one holder outside Microsoft and OpenTelemetry, is never
referenced directly. Only packages that put a runtime, native, or resource file into the build count, so
an analyzer, or the trimming tasks the SDK restores for a single-file publish, is not listed; the result
is the same whichever restore last wrote the assets file. A second test checks that the dashboard's
source imports only packages its `package.json` declares under `dependencies`, because the bundler takes
whatever the source imports, and the production graph is sound only if that holds.

### 4. Every declaration says the same thing

Several places now name the license or the repository:

- the dashboard's `package.json` declares `"license": "Apache-2.0"`;
- the OpenAPI document's `info.license` declares the same SPDX identifier;
- the image declares it in its `org.opencontainers.image.licenses` label;
- the image's `org.opencontainers.image.source` label is the repository `Directory.Build.props` names.

That repository URL was wrong: it named `agentsplice/agentsplice`, which does not exist. It is now the
repository the project lives in, `murillojndem/agent-splice`. Contract tests hold all of these to one
value.

### 5. Contributions come in under the license they go out under

`CONTRIBUTING.md` says what section 5 of the license provides: unless a contributor explicitly states
otherwise, a contribution intentionally submitted for inclusion is licensed under the same terms, without
additional ones. Contributions offered under other terms are not accepted. There is no contributor
license agreement and no sign-off requirement. A new dependency must be under a compatible license and be
listed in `THIRD-PARTY-NOTICES.md` in the same change.

### 6. A channel for reports that works

GitHub's private vulnerability reporting is enabled for the repository, and `SECURITY.md` points at it
through the repository `Directory.Build.props` names. It names no personal address. The issue chooser
links there too.

The issue forms take `description`, and they apply only labels GitHub creates in every repository: they
named `needs-triage`, which the repository never had, so an issue filed through either form would have
been filed without it. A bug report now asks for the AgentSplice version and how it was installed, both
required. A test holds every form to the rules of GitHub's form schema whose breach makes GitHub drop it.

## Consequences

- GitHub detects the repository's license as Apache-2.0, and the release workflow can run.
- What the project distributes carries the terms it is distributed under, and the notices of the code it
  bundles.
- Adding a shipped dependency now includes adding its notice; the build fails until that is done.
- The NOTICE, the assemblies, and the image name the copyright holder and the real repository.
- No runtime behaviour changes. No request-path code, log, metric, or configuration validation is
  touched, so those parts of the definition of done do not apply.

## Tests proving these decisions

- `LicenseContractTests`:
  - `LICENSE` is the Apache License 2.0 word for word, by SHA-256;
  - exactly one file at the root reads as a license, and there is no `LICENSES` directory;
  - `package.json`, the OpenAPI document, and the image's label name the same SPDX identifier;
  - the image's source label is the build's `RepositoryUrl`, and `SECURITY.md` points at that
    repository's private reporting;
  - the image's description fits the 512 characters GitHub shows;
  - `NOTICE` names the holder the build declares;
  - every package that ships a file in the gateway's restored graph, and every package in the
    dashboard's production graph, is in the Components table or the list of packages that do not ship,
    and not both, with guards that the graphs are the transitive ones and leave out what ships nothing;
  - the dashboard's source imports only its production dependencies;
  - the image copies the three files into its runtime stage;
  - the package script copies them and refuses to build without them;
  - the release workflow refuses to run without them, before anything is built.
- `IssueFormContractTests`:
  - every form has a name, a description, no `about`, and a body whose elements are of a type GitHub
    knows, with a label, a markdown value, distinct choices, and distinct well-formed IDs;
  - the bug form requires the version and the installation;
  - the forms apply only labels GitHub creates;
  - the chooser links vulnerabilities to private reporting.
- The CI `package-windows` job — the smoke test finds the three files, identical to the repository's,
  and the runtime's three notices in `licenses/`.
- The CI `image` job — the running image's `/app` copies of the three files are the repository's, and
  its labels name Apache-2.0 and, on a tag, this repository.

Each fails against its reintroduced defect:

- a byte added to `LICENSE`, a second license file at the root, or a `LICENSES` directory;
- `MIT` in `package.json`, in the image's label, or in the OpenAPI document;
- the old repository in the source label or in `SECURITY.md`'s link;
- a description past 512 characters, or another year in `NOTICE`;
- `scheduler`, `SQLitePCLRaw.*`, or the bare `OpenTelemetry` missing from the table, `js-tokens` missing
  from the list of packages that do not ship, a package in both, and a package named only in prose;
- the dashboard importing a package it declares only as a development dependency;
- the image, the script, or the release checking `LICENSE` alone;
- `about` back in an issue form, an unknown element type, a dropdown without choices, the version no
  longer required, `needs-triage` back, or the chooser linking to public issues.

## Alternatives considered

- **MIT.** Shorter and as permissive, but with no patent grant and no NOTICE mechanism. Rejected: the
  maintainer chose Apache-2.0.
- **Keeping `LICENSE-CHOICE.md` as a record.** GitHub would keep reporting "Other". The reasoning lives
  here instead.
- **Generating the notices at build time** from NuGet's cache and `pnpm licenses list`. Several packages
  ship no license text, and a nuspec's `<copyright>` can disagree with the license's own notice, as
  Entity Framework Core's does, so the output would still need the review a curated file gets — and it
  would be a second build step to maintain. A curated file kept complete by a test can be reviewed once
  and then changes only with the dependencies.
- **A contributor license agreement or a sign-off requirement.** Neither is needed for the license to
  apply to contributions, and each adds a step before a first pull request.
- **A personal e-mail address as the security contact.** It would publish an address, and GitHub's
  private reporting keeps the report and the advisory in one place.
