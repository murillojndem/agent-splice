# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /source

# Restore against the manifests first so that a source-only change reuses the package layer.
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/AgentSplice.Api/AgentSplice.Api.csproj src/AgentSplice.Api/
COPY src/AgentSplice.Application/AgentSplice.Application.csproj src/AgentSplice.Application/
COPY src/AgentSplice.Domain/AgentSplice.Domain.csproj src/AgentSplice.Domain/
COPY src/AgentSplice.Infrastructure/AgentSplice.Infrastructure.csproj src/AgentSplice.Infrastructure/
RUN dotnet restore src/AgentSplice.Api/AgentSplice.Api.csproj

COPY src/ src/
RUN dotnet publish src/AgentSplice.Api/AgentSplice.Api.csproj \
      --configuration Release \
      --no-restore \
      --output /publish


FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# The process binds to the container interface; loopback-only exposure is the operator's port
# mapping (see docker-compose.yml and docs/SECURITY.md).
ENV ASPNETCORE_URLS=http://0.0.0.0:5280 \
    DOTNET_NOLOGO=true \
    DOTNET_CLI_TELEMETRY_OPTOUT=true

COPY --from=build /publish ./

# /data holds the SQLite metadata store. The image runs as the non-root user shipped in the .NET 8
# base image, so the volume mount point must be writable by it.
RUN mkdir -p /data && chown -R app:app /data /app
USER app

EXPOSE 5280

# No HEALTHCHECK yet: liveness and readiness endpoints arrive with Stage 1 (FR-HEALTH-001), and a
# healthcheck against a route that does not exist would report a permanently unhealthy container.
ENTRYPOINT ["dotnet", "AgentSplice.Api.dll"]
