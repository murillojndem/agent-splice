# Third-party notices

AgentSplice is licensed under the Apache License, Version 2.0 ([LICENSE](LICENSE)). What it distributes also carries
third-party software, each under its own license: the Windows and Linux packages a release publishes, and the
container image, which a release carries as an archive and pushes to GitHub Container Registry. Each carries this
file. Direct NuGet dependencies are pinned in
`Directory.Packages.props` and transitive ones are what the restore resolves at the release's tag; the dashboard's
are pinned by `web/AgentSplice.Dashboard/pnpm-lock.yaml`.

- **The Windows and Linux packages** are self-contained, so they also carry the .NET runtime and ASP.NET Core. Each
  package's `licenses/` folder holds the runtime pack's own license and third-party notices and ASP.NET Core's third-party notices, as
  Microsoft ships them in the runtime packs the package was published with. ASP.NET Core 8's pack carries no
  separate license file: ASP.NET Core is MIT under the same holder as the runtime, whose notice is below.
- **The container image** is built on Microsoft's `mcr.microsoft.com/dotnet/aspnet:8.0` image, which carries the
  .NET notices under `/usr/share/dotnet/`. Its operating-system layer is Debian's: each package's copyright and
  license are under `/usr/share/doc/<package>/copyright`, and some of those packages are under the GPL or the LGPL.
  The image lists the exact packages and versions it holds in `/var/lib/dpkg/status`, and Debian publishes the
  source of every version it has released at <https://snapshot.debian.org>.

A contract test fails if a package that ships a file in the gateway's restored dependency graph, or a package in the
dashboard's production dependency graph, is in neither the Packages column of the table below nor the list of
packages that do not ship. A name ending in `.*` in the table stands for every package whose name starts with what
precedes the asterisk.

## Components

| Component | Packages | License | Copyright |
| --- | --- | --- | --- |
| .NET runtime and ASP.NET Core | in the Windows and Linux packages; in the image's base layer | MIT | .NET Foundation and Contributors |
| Entity Framework Core and Microsoft.Data.Sqlite | `Microsoft.EntityFrameworkCore`, `Microsoft.EntityFrameworkCore.*`, `Microsoft.Data.Sqlite.Core` | MIT | .NET Foundation and Contributors |
| Microsoft.Extensions | `Microsoft.Extensions.*` | MIT | .NET Foundation and Contributors |
| System libraries | `System.Diagnostics.DiagnosticSource`, `System.Diagnostics.EventLog`, `System.Memory` | MIT | .NET Foundation and Contributors |
| OpenTelemetry .NET | `OpenTelemetry`, `OpenTelemetry.*` | Apache-2.0 | The OpenTelemetry Authors |
| SQLitePCLRaw | `SQLitePCLRaw.*` | Apache-2.0 | 2014-2023 SourceGear, LLC |
| SQLite | compiled into `SQLitePCLRaw.lib.e_sqlite3`: `e_sqlite3.dll` in the Windows package, `libe_sqlite3.so` in the Linux package and among the image's native libraries | Public domain | — |
| React | `react`, `react-dom`, `scheduler`, bundled into the dashboard | MIT | Facebook, Inc. and its affiliates |
| React Router | `react-router-dom`, `react-router`, `@remix-run/router`, bundled into the dashboard | MIT | React Training LLC 2015-2019; Remix Software Inc. 2020-2021; Shopify Inc. 2022-2023 |
| TanStack Query | `@tanstack/react-query`, `@tanstack/query-core`, bundled into the dashboard | MIT | 2021-present Tanner Linsley |
| Build tools | `vite`, `rollup`, `esbuild`: the dashboard is built with them, and the code they generate into its bundle — Vite's module-preload polyfill and esbuild's helpers among it — ships | MIT | 2019-present, VoidZero Inc. and Vite contributors; 2017 Rollup contributors; 2020 Evan Wallace |

## Packages that do not ship

These are in a dependency graph the contract test reads, but no artifact carries them:

- `loose-envify` — React's packages declare it as a dependency for builds with Browserify. The dashboard's build
  never runs it, so none of its code is bundled.
- `js-tokens` — a dependency of `loose-envify`, for the same reason.

## MIT License

The components above licensed under the MIT License carry these copyright notices:

- Copyright (c) .NET Foundation and Contributors
- Copyright (c) Facebook, Inc. and its affiliates.
- Copyright (c) React Training LLC 2015-2019
- Copyright (c) Remix Software Inc. 2020-2021
- Copyright (c) Shopify Inc. 2022-2023
- Copyright (c) 2021-present Tanner Linsley
- Copyright (c) 2019-present, VoidZero Inc. and Vite contributors
- Copyright (c) 2017 [these people](https://github.com/rollup/rollup/graphs/contributors)
- Copyright (c) 2020 Evan Wallace

and this permission notice:

```text
Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Apache License 2.0

OpenTelemetry .NET and SQLitePCLRaw are licensed under the Apache License, Version 2.0, the license AgentSplice itself
uses; its full text is in [LICENSE](LICENSE). Neither ships a `NOTICE` file.

OpenTelemetry .NET passes on these notices for code it adapted from other projects:

- **.NET** — Copyright (c) .NET Foundation and Contributors, under the MIT License above.
- **gRPC for .NET** (https://github.com/grpc/grpc-dotnet) — Copyright 2019 The gRPC Authors, under the Apache License,
  Version 2.0.

## SQLite

SQLite is in the public domain. In place of a legal notice, its authors offer a blessing:

```text
May you do good and not evil.
May you find forgiveness for yourself and forgive others.
May you share freely, never taking more than you give.
```
