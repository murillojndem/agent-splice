# ADR 0015 — Stage 1D: the gateway serves the dashboard

- Status: Accepted
- Date: 2026-10-09
- Related: [ADR 0001](0001-modular-monolith.md) (modular monolith), [ADR 0007](0007-stage-0-build-and-test-toolchain.md)
  (no placeholder responses; loopback as a fallback), [ADR 0013](0013-stage-1c-metadata-store.md)
  (administrative authentication), [ADR 0014](0014-stage-1d-opentelemetry-export.md) (no framework
  instrumentation)

## Context

The Stage 1C dashboard runs only under `pnpm dev`: Vite serves it on port 5281 and proxies `/api` to
the gateway on 5280. The container image contains no dashboard, nothing publishes a build of one, and
the gateway served no static file. Someone who installs the alpha therefore has no way to open the
one surface built for looking at evidence, while `docs/SPECIFICATION.md` section 22 lists a dashboard
showing the four Stage 1C screens, and a documented Docker and direct-process setup, among the
alpha's acceptance criteria.

The dashboard was written as though it would be served from the gateway's origin even though
nothing served it there: `src/api/client.ts` calls `/api/v1/...` by relative path, there is no base
URL setting, and the gateway has no CORS. The comment in `vite.config.ts` and `web/README.md` said the
two were "configured by URL" in production; no such configuration existed.

## Decisions

### 1. Same process, same origin, same image

The gateway serves the built dashboard itself. A separate web server — an nginx container beside the
gateway — would need CORS or a second reverse proxy, a second image, and a second port, against
ADR 0001's modular monolith and for no capability the gateway lacks. The token the dashboard sends is
a bearer header held in memory, not a cookie, so sharing an origin with the administrative API adds no
cross-site request forgery exposure.

The gateway still builds, tests, and runs without Node, `pnpm`, or a build (FR-DASH-002). Only the
image build needs Node, and it brings its own.

### 2. A fixed mount at `/dashboard/`, and a redirect from `/` only while it is served

Everything the dashboard is lives under `/dashboard/`. The mount is not configurable: Vite writes it
into every asset URL of the build and into the client router's basename, so a configurable mount
would need the shell rewritten at runtime.

`GET` and `HEAD` on `/` answer 302 to `/dashboard/` while the dashboard is served, so a browser pointed
at the gateway lands on it. With no dashboard, `/` stays a 404: a page saying "nothing here" is the
placeholder ADR 0007 rejects, because a client cannot tell it from a working gateway. The redirect is
temporary (302) so that a browser does not keep it after the dashboard is turned off.

### 3. One middleware branch, with explicit rules, and nothing outside it changes

The dashboard is a `Map("/dashboard")` middleware branch, not an endpoint:

| Request under `/dashboard` | Answer |
|---|---|
| `/dashboard` | 302 to `/dashboard/`, keeping the query |
| a file that exists in the build | the file |
| anything else under `/assets/` | 404 |
| any other path | the shell, `index.html` |
| a method other than `GET` or `HEAD` | 405 with `Allow: GET, HEAD` |

The shell goes through the same static file middleware as every other file, so it keeps its ETag,
conditional requests, and `HEAD` handling. Inside the branch the mount is the path base, so the static
file options name no request path and cannot get one wrong.

A missing asset is a 404 rather than the shell, because a browser that asked for a script and receives
HTML refuses it under `nosniff` with an error pointing nowhere near the cause. Every other path gets the
shell — dots included, because runtime and model identifiers carry them and a future route naming one
must survive a reload.

`/dashboard` without the slash is redirected because the router's basename carries the slash, which
makes the Overview link `/dashboard/` — the canonical URL — and renders nothing at `/dashboard`.

### 4. Every dashboard response carries a strict policy (FR-DASH-008)

```text
Content-Security-Policy: default-src 'none'; script-src 'self'; style-src 'self'; img-src 'self' data:;
  font-src 'self'; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'
X-Content-Type-Options: nosniff
X-Frame-Options: DENY
Referrer-Policy: no-referrer
```

The dashboard renders strings that clients and runtimes chose, on the origin of the administrative
API, while holding the operator's token. React's text rendering is the first defence (THREAT_MODEL
"Dashboard XSS"); the policy is the second, so that a rendering mistake cannot become script that reads
the token or the evidence. It admits exactly what the production build uses — one same-origin module
script, one stylesheet, same-origin `fetch` — and was checked against the built bundle: no inline
script, no `eval`, no injected `<style>`. The waterfall's bar widths are set through the CSSOM, which
the policy does not restrict. The token form calls `preventDefault`, so `form-action 'none'` never
fires. The headers are set on every response under the mount, refusals and redirects included, so no
response there is the one that was forgotten.

### 5. Hashed files are immutable; the shell is revalidated

Files under `/dashboard/assets/` carry a content hash in their names, and are served with
`Cache-Control: public, max-age=31536000, immutable`. Everything else — the shell, whether requested
as a client route or by name — is `no-cache`, so a new build is picked up on the next load and an
unchanged one costs a 304. Vite's `build.assetsDir` is set explicitly in `vite.config.ts`, because it is
now half of this contract.

### 6. The page needs no credential; the evidence still does

The shell and its assets are the same public code for every caller and hold no evidence, so they are
served without the administrative token. The evidence stays behind the `/api/v1` endpoint filter,
unchanged: with a token configured every caller presents it, loopback included (ADR 0013), and the
dashboard asks for it in its token field and keeps it in memory. Gating the shell on the token would
need a cookie or a login page, which is a session design this stage does not have.

### 7. Optional: a missing build is announced, not refused

`agentsplice:dashboard` has two settings:

```yaml
agentsplice:
  dashboard:
    enabled: true        # default
    rootPath: dashboard  # default; relative to the content root, /app/dashboard in the image
```

A directory is a build only if it holds `index.html` and an `assets` directory; the dashboard's
source directory has an `index.html` too, and serving it would serve a page that loads nothing beside
the source tree. With no build, the gateway starts, serves every API, maps nothing for the dashboard,
and logs `1204 DashboardBuildMissing` once, naming the absolute path it checked. Refusing to start
would break every gateway run from source before `pnpm build`, for a component FR-DASH-002 makes
optional. Serving logs `1203 DashboardServed` with the directory. `enabled: false` maps nothing and logs
nothing: a deployment that ships the build but wants no browser-facing surface has made a choice, not
a mistake. A blank `rootPath` fails validation, enabled or not.

### 8. The image builds the dashboard in its own stage

The Dockerfile's first stage is `node:22-bookworm-slim` on the build platform — the output has no
architecture — with `pnpm` pinned, a frozen lockfile install, and `pnpm build`. The runtime stage
copies `dist` to `/app/dashboard`, where the shipped `rootPath` looks. `.dockerignore` now anchors
`node_modules`, `dist`, and the SQLite files at every depth: a pattern without `**/` matches only at
the context root, so the host's `node_modules` — native binaries for the host's platform — would have
overwritten the stage's own install, and a development `agentsplice.db` would have entered the context.

The .NET build does not invoke Node in any form: no MSBuild target, no `.esproj`, no SPA proxy.

### 9. CI builds the image and drives it the way an alpha user would

A new `image` job runs `docker compose up --build` with a generated token and runs
`.github/scripts/smoke-image.sh` against it: the redirect, the shell and its headers, every asset the
shell names (typed and immutable), a deep link, a missing asset, traversal attempts through real Kestrel
normalisation, the unchanged API answers, and the startup log's 1203 with no 1204. It is the only
automated proof that the artifact an alpha user installs serves the dashboard it was built with.

## Consequences

- An alpha user opens `http://127.0.0.1:5280/` and lands on the dashboard; no Node is needed anywhere
  but in the image build, which now also needs the npm registry.
- The development URL moves to `http://127.0.0.1:5281/dashboard/`. The dev server uses the same base,
  so a deep link means the same thing there as on the gateway.
- A gateway run from source logs 1204 until it is pointed at a build. Every integration test boots
  that way, so the suite also proves continuously that a missing build leaves the APIs alone.
- Framing is refused, so an editor's embedded browser that renders pages in an iframe cannot show the
  dashboard. That is the price of no other page being able to overlay it.
- Adopting a CSS-in-JS library that injects `<style>` elements (Emotion, and therefore MUI, which
  `docs/SPECIFICATION.md` section 19.3 suggests) would need a nonce strategy first.
- The gateway must sit at the root of its origin. Asset URLs, the API calls, and the redirect are all
  absolute paths, so a reverse proxy that maps AgentSplice under a path prefix is unsupported — as the
  API client already was.
- Dashboard responses carry no correlation identifiers and produce no spans or metrics: they are not
  exchanges, and framework instrumentation stays off (ADR 0014 decision 7).
- `rootPath` is trusted to the operator: every file beneath it with a known content type is served
  (`docs/SECURITY.md`).
- Measured against a fixture build in the in-process test server and against the real image on a
  development workstation. Neither is a performance claim.

## Tests proving these decisions

- `DashboardServingTests` — the shell, the bare-mount redirect, deep links including a dotted segment,
  immutable assets, missing assets as 404, revalidation with 304, the headers on every response kind,
  `HEAD`, 405, traversal and hidden files, the root redirect, the shell without a credential beside a
  401 from `/api/v1`, every API answer compared with a gateway serving no dashboard, and event 1203.
  The comparison test fails against a root `MapFallbackToFile`, which turns `GET /v1/chat/completions`
  from 405 into 200.
- `DashboardOptionalityTests` — a missing build, an index without assets, the switch off, and relative
  and absolute `rootPath`.
- `DashboardBuildContractTests` — Vite's `base` and `assetsDir` match the gateway's mount and cached
  directory.
- `PackagingContractTests` — the Dockerfile copies the build where the shipped `rootPath` looks, and
  `.dockerignore` anchors its patterns at every depth. Both fail against a reintroduced defect.
- `tests/mount.test.tsx` in the dashboard — the real root recognises deep links beneath the mount and
  links every screen beneath it.

## Alternatives considered

- **A separate nginx container.** Rejected in decision 1.
- **A fallback endpoint at the root** (`MapFallbackToFile("index.html")`). It changes answers outside
  the dashboard: `GET /v1/chat/completions` becomes a 200 page instead of a 405, `HEAD /health/live` a
  200 instead of a 405, and an unknown `/api/v1` route a 200 page the dashboard's own client would read
  as an empty result. Every later surface would need adding to an exclusion list.
- **A fallback endpoint scoped to `/dashboard/{**path:nonfile}`.** Confined, but it rewrites the path
  to the shell, which silently 404s if the static file options carry a request path; it inherits a
  "looks like a file" rule that breaks dotted routes on reload; and it puts the static file middleware
  in front of every API request.
- **Embedding the build in the assembly, or using `wwwroot` and Static Web Assets.** Both make the .NET
  build depend on the dashboard's output being present, so either the .NET build needs Node or what a
  build contains depends on what was lying around.
- **SPA proxy, `.esproj`, or an MSBuild target running `pnpm`.** Rejected for the same reason: the
  gateway builds without Node (FR-DASH-002).
- **A token-gated shell.** Rejected in decision 6.
- **Choosing the shell by the `Accept` header.** Clients send inconsistent `Accept` values for
  navigations and subresources, and a rule a curl user cannot predict is worse than one keyed on path.
