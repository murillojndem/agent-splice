# ADR 0016 — Stage 1D: the image reports its health

- Status: Accepted
- Date: 2026-10-15
- Related: [ADR 0001](0001-modular-monolith.md) (modular monolith), [ADR 0007](0007-stage-0-build-and-test-toolchain.md)
  (the image context; loopback as a fallback), [ADR 0014](0014-stage-1d-opentelemetry-export.md) (no
  framework instrumentation), [ADR 0015](0015-stage-1d-gateway-serves-the-dashboard.md) (CI drives the
  image as an alpha user would)

## Context

The image had no `HEALTHCHECK`. The Dockerfile said why: `mcr.microsoft.com/dotnet/aspnet:8.0` ships no
HTTP client — no curl, no wget, no busybox — so there was nothing to run one with, and it told operators
to probe `/health/live` from outside the container. Docker therefore reported every AgentSplice
container as merely running, `docker compose up --wait` and `depends_on: condition: service_healthy`
could not wait for the gateway, and CI waited with a `curl` loop on the runner instead of with anything
the image itself carried.

The gateway already has the endpoint a container needs. `GET /health/live` touches no runtime and no
database and answers `204` (FR-HEALTH-001, FR-HEALTH-002); it answers `HEAD` with `405`. What was
missing was a client inside the image to ask it.

The .NET container images have no official health-check tool; the maintainers' suggestion is a small
.NET application that makes the request and returns 0 or 1 (dotnet/dotnet-docker#4300). The gateway
assembly cannot be that application without cost: its `runtimeconfig.json` selects server GC, which
creates a heap and a collector thread per core in a process that lives for one request, and an
architecture test forbids `HttpClient` in `AgentSplice.Api`, because the transport layer opens no
upstream connections.

## Decisions

### 1. A separate probe that references only the base class library

`src/AgentSplice.HealthProbe` is a console application with no project or package references. It is
published into the image at `/app/probe/`, beside the gateway rather than inside its directory, so that
neither application's `runtimeconfig.json` or `deps.json` can shadow the other's. It runs with
workstation, non-concurrent GC and invariant globalization. Nothing references it, and it references no
AgentSplice module; an architecture test enforces both directions. It is a command the image runs, not
a library the gateway calls.

### 2. One `GET`, any 2xx, loopback only

The probe takes exactly one argument, an absolute `http` URL whose host is a loopback IP literal —
127.0.0.0/8 or `::1` — with no user information. Anything else is refused before any request is made.
`localhost` is refused too: resolving a name would make the answer depend on the container's resolver.

It sends one `GET`, because the endpoint answers `HEAD` with `405`, through a handler that uses no
proxy and follows no redirect, so neither an ambient `HTTP_PROXY` nor a `302` can make the answer come
from somewhere else. It reads the status line and headers and never the body. Any 2xx is healthy,
because liveness answers `204`; any other status, a refused connection, or no answer within three
seconds is unhealthy.

### 3. Exit codes Docker defines, and one line that says why

The probe exits `0` or `1` and nothing else: Docker reserves `2`, and an unhandled exception would exit
with a code Docker does not define, so every failure, expected or not, becomes `1`. Every run writes one
ASCII line — `ok: GET … answered 204 in 3 ms`, `unhealthy: GET … answered 503 …`, `… had no answer within
3000 ms`, `… failed: ConnectionRefused` — which Docker keeps in `docker inspect`'s health log. The line
never echoes a target the probe refused, so a credential in a mistyped URL does not end up there.

### 4. Liveness, on the image's own port, inside Docker's timeout

```dockerfile
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --start-interval=2s --retries=3 \
    CMD ["dotnet", "/app/probe/AgentSplice.HealthProbe.dll", "http://127.0.0.1:5280/health/live"]
```

Exec form, because a shell form needs `/bin/sh`, which a chiseled image does not have. Liveness and
never readiness: readiness answers `503` when a deployment requires a reachable runtime and none
answers, and a container marked unhealthy for that would report LM Studio's outage as the gateway's.
The probe's three-second budget is shorter than Docker's five, so a stalled gateway is reported with a
reason rather than killed with nothing said. The start period covers migrations, which run before
Kestrel binds; the start interval, which needs Docker Engine 25, lets `--wait` return within a couple of
seconds of the gateway listening.

The target port is the one `ASPNETCORE_URLS` names in the image and in Compose, and the one the image
exposes. A deployment that moves the gateway to another container port must override the health check
too; the host side of a port mapping does not matter.

### 5. Health is reported, not acted on

Outside Swarm, Docker restarts nothing because a container is unhealthy. What the status gates is
`docker compose up --wait` and `depends_on: condition: service_healthy`, and what it informs is
`docker compose ps`. The Compose file declares no `healthcheck` of its own, so what runs under Compose
is the image's check. AgentSplice adds no restart policy and no watchdog.

### 6. CI waits on the image's own check

The CI `image` job starts the stack with `docker compose up --build --wait`, which returns only once the
image's health check reports healthy, in place of the `curl` loop on the runner. After the smoke test it
checks the reported status and proves the probe can fail: against a port nothing listens on and
against a target it must refuse, it exits exactly `1`. A health check that always passed would have
satisfied `--wait` just as well.

## Consequences

- The image grows by the probe's assembly, a few kilobytes, and runs one short-lived `dotnet` process
  every thirty seconds. Its cost was not measured, and no performance claim is made.
- The gateway logs nothing for a probe at the shipped log levels, and exports nothing for it
  (ADR 0014 decision 7). What the probe reported is in Docker's health log, not AgentSplice's.
- Changing the container's port now means changing two settings, and a contract test keeps what this
  repository ships consistent.
- The probe would run unchanged on a chiseled base image, which has no shell; moving to one is not part
  of this decision.

## Tests proving these decisions

- `LivenessProbeTests` — a 2xx is healthy and is asked with `GET`; non-2xx statuses are unhealthy and
  named; a redirect is reported rather than followed; a stall expires on the probe's own clock; a refused
  connection is named; off-loopback, non-`http`, and malformed targets, `localhost` among them, and the
  wrong number of arguments are refused without a request; an unexpected fault is still `1`; and the
  probe accepts the gateway's own liveness answer. That last test fails against a probe that requires
  exactly `200` or asks with `HEAD`.
- `ImageHealthCheckContractTests` — the `HEALTHCHECK` is the only one, sits in the runtime stage, runs
  the probe in exec form against `/health/live`, names a target the probe accepts on the port the image
  and Compose listen on, runs the probe from where the image copies it, gives the probe a budget shorter
  than Docker's timeout, and is not overridden by Compose. Each fails against its reintroduced defect: a
  `curl` command, `localhost`, port 8080, `/health/ready`, a two-second timeout, a different copy target,
  and a Compose override.
- `ModuleBoundaryTests` — the probe references only the base class library, and no gateway assembly
  references it.
- The CI `image` job — the container goes healthy, and the probe exits `1` against a dead port and a
  refused target.

## Alternatives considered

- **Installing curl or wget in the runtime image.** It adds packages and their vulnerability reports to
  every image for one request, and it rules out a chiseled base later.
- **A `bash` one-liner over `/dev/tcp`.** It needs a shell, writes HTTP by hand, has no timeout of its
  own, and is unreadable in a review.
- **A probe mode in the gateway executable** (`dotnet AgentSplice.Api.dll healthcheck`). Every run would
  load the gateway's assemblies with server GC, the dispatch would sit in front of the host every
  integration test boots, and the HTTP call would need a home outside `AgentSplice.Api` that the
  architecture test reserves for the provider.
- **A health check declared only in Compose.** An image run without Compose would report nothing, and
  Compose's check would still need a client inside the image.
- **Probing readiness.** Rejected in decision 4.
