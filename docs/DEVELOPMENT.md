# Development guide

## Prerequisites

- .NET 8 SDK
- Docker Desktop or compatible Docker Engine
- Git
- PowerShell 7 recommended on Windows
- Node.js and pnpm only when the dashboard exists

## Bootstrap plan

```powershell
dotnet new sln -n AgentSplice
# Create projects according to docs/ARCHITECTURE.md
# Add project references following dependency rules

dotnet restore
dotnet build
dotnet test
```

## Local runtime

LM Studio commonly listens at `http://127.0.0.1:1234/v1`. When AgentSplice runs in Docker Desktop, the upstream host may be configured as `http://host.docker.internal:1234/v1`.

## Configuration

Copy `.env.example` to `.env` for Compose. Never commit a real API key.

## Database

SQLite is sufficient for first local development. PostgreSQL Compose profile is provided as a planning scaffold.

## Branching

Use short-lived feature branches. Reference specification requirement IDs in commits or pull requests.

## Documentation

Architecture-significant changes require an ADR. User-visible behavior changes require API/specification updates.
