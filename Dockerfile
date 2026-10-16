# syntax=docker/dockerfile:1

# The dashboard, built here so that installing the image needs no Node toolchain (ADR 0015). It runs
# on the build platform because its output — HTML, JavaScript, and CSS — has no architecture, and on
# Debian rather than Alpine for the same glibc the CI dashboard job builds against.
FROM --platform=$BUILDPLATFORM node:22-bookworm-slim AS dashboard
WORKDIR /dashboard

# An exact pnpm, so a new release cannot reinterpret the lockfile under an unchanged Dockerfile.
ARG PNPM_VERSION=10.30.0
RUN npm install --global pnpm@${PNPM_VERSION}

# Manifests first, so a source-only change reuses the dependency layer.
COPY web/AgentSplice.Dashboard/package.json web/AgentSplice.Dashboard/pnpm-lock.yaml ./
RUN pnpm install --frozen-lockfile

# .dockerignore keeps the host's node_modules and dist out of this copy at any depth. A host
# node_modules copied over the one just installed carries the host's native binaries, and on a
# Windows checkout that breaks the build below.
COPY web/AgentSplice.Dashboard/ ./
RUN pnpm build


FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Restore against the manifests first so that a source-only change reuses the package layer.
#
# .editorconfig is part of the build contract, not an editor preference: Directory.Build.props turns
# warnings into errors, and the analyser severities this repository sets — CA1848 among them — live
# there. Without it the container build fails on rules the solution has deliberately waived.
COPY global.json Directory.Build.props Directory.Packages.props .editorconfig ./

# Every project the API transitively references. A missing manifest here does not fail the restore;
# it fails the publish below with NETSDK1004, because --no-restore then finds no assets file for a
# project nobody restored.
COPY src/AgentSplice.Api/AgentSplice.Api.csproj src/AgentSplice.Api/
COPY src/AgentSplice.Application/AgentSplice.Application.csproj src/AgentSplice.Application/
COPY src/AgentSplice.Domain/AgentSplice.Domain.csproj src/AgentSplice.Domain/
COPY src/AgentSplice.Infrastructure/AgentSplice.Infrastructure.csproj src/AgentSplice.Infrastructure/
COPY src/AgentSplice.Observability/AgentSplice.Observability.csproj src/AgentSplice.Observability/
COPY src/AgentSplice.Protocols.OpenAI/AgentSplice.Protocols.OpenAI.csproj src/AgentSplice.Protocols.OpenAI/
COPY src/AgentSplice.Providers.LmStudio/AgentSplice.Providers.LmStudio.csproj src/AgentSplice.Providers.LmStudio/
# The health probe the image runs as its HEALTHCHECK (ADR 0016). It references nothing, so it is
# restored on its own; the gateway does not reference it either.
COPY src/AgentSplice.HealthProbe/AgentSplice.HealthProbe.csproj src/AgentSplice.HealthProbe/
RUN dotnet restore src/AgentSplice.Api/AgentSplice.Api.csproj \
 && dotnet restore src/AgentSplice.HealthProbe/AgentSplice.HealthProbe.csproj

COPY src/ src/
RUN dotnet publish src/AgentSplice.Api/AgentSplice.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /publish

# Published apart from the gateway so that each keeps its own runtimeconfig: the probe runs with
# workstation GC, the gateway with server GC.
RUN dotnet publish src/AgentSplice.HealthProbe/AgentSplice.HealthProbe.csproj \
      --configuration Release \
      --no-restore \
      --output /publish-probe


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# What a registry shows for the image and links it to (ADR 0020). The source is the
# repository Directory.Build.props names, and the license the SPDX identifier the dashboard's
# package.json declares; a contract test holds all three to one value. GitHub's package page shows at
# most 512 characters of the description.
LABEL org.opencontainers.image.title="AgentSplice" \
      org.opencontainers.image.description="An experimental alpha: a local trace proxy for OpenAI-compatible model runtimes such as LM Studio. It forwards requests unchanged and records where the time went, never prompts or model output." \
      org.opencontainers.image.source="https://github.com/murillojndem/agent-splice" \
      org.opencontainers.image.licenses="Apache-2.0"

# The process binds to the container interface; loopback-only exposure is the operator's port
# mapping (see docker-compose.yml and docs/SECURITY.md).
ENV ASPNETCORE_URLS=http://0.0.0.0:5280 \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

COPY --from=build /publish ./

# Where the shipped agentsplice:dashboard:rootPath ("dashboard", relative to the content root /app)
# looks for it. Copied before the ownership change below, like everything else under /app: a
# root-owned dashboard inside an app-owned /app would protect nothing, because the owner of a
# directory can replace what is in it.
COPY --from=dashboard /dashboard/dist ./dashboard/

# The health probe, beside the gateway rather than inside its directory, so that neither's
# runtimeconfig.json or deps.json can shadow the other's. Under /app like everything else, for the
# same ownership reason as the dashboard.
COPY --from=build /publish-probe ./probe/

# The license the image is distributed under, beside what it covers. The .NET runtime's own notices are
# in the base image under /usr/share/dotnet.
COPY LICENSE NOTICE THIRD-PARTY-NOTICES.md ./

# /data holds the SQLite metadata store. The image runs as the non-root user shipped in the .NET 8
# base image, so the volume mount point must be writable by it.
RUN mkdir -p /data && chown -R app:app /data /app
USER app

EXPOSE 5280

# The aspnet base image ships no HTTP client — no curl, no wget — so the image carries its own probe
# (ADR 0016). Exec form, because a shell form needs /bin/sh, which a chiseled image does not have.
# Liveness, not readiness: liveness touches no runtime, so LM Studio being down never makes the
# gateway unhealthy (FR-HEALTH-002). The probe gives up after 3 s, inside Docker's 5 s, so a stalled
# gateway is reported with a reason rather than killed silently. Docker acts on the result only by
# reporting it — `docker compose up --wait` and `depends_on: condition: service_healthy` wait for it;
# outside Swarm nothing restarts an unhealthy container.
#
# The port here is the one ASPNETCORE_URLS names above; a deployment that changes it must override
# the health check too, which a contract test keeps true of what this repository ships.
HEALTHCHECK --interval=30s --timeout=5s --start-period=30s --start-interval=2s --retries=3 \
    CMD ["dotnet", "/app/probe/AgentSplice.HealthProbe.dll", "http://127.0.0.1:5280/health/live"]

ENTRYPOINT ["dotnet", "AgentSplice.Api.dll"]
