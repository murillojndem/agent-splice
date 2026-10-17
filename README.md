# AgentSplice

**Local agent interoperability, observability, and evaluation.**

AgentSplice is a local-first platform for tracing, replaying, comparing, and evaluating interactions between AI-agent clients and model runtimes.

It sits between clients such as OpenCode, Cline, Aider, IDE extensions, test harnesses, and automation scripts, and runtimes such as LM Studio, llama.cpp, Ollama, vLLM, SGLang, or other compatible services.

```text
Agent client
  OpenCode / Cline / Aider / custom harness
          |
          | OpenAI or Anthropic-compatible request
          v
+------------------------------------------------+
|                  AgentSplice                   |
| trace capture and timeline reconstruction      |
| protocol and tool-call conformance             |
| sanitized replay and differential comparison  |
| agent evaluation and regression testing        |
| OpenTelemetry export and local dashboard       |
| optional compatibility adapters                |
+------------------------------------------------+
          |
          | transparent or explicitly adapted request
          v
Model runtime
  LM Studio / llama.cpp / Ollama / vLLM / SGLang
```

## Quickstart

AgentSplice is an experimental alpha. Today it serves the OpenAI Chat Completions endpoints in front of
an LM Studio runtime and records structural evidence of every exchange. Replay, conformance runs,
evaluation, Anthropic ingress, other runtimes, and compatibility adapters are planned, not present
([Status](#status)). No compatibility claim is made for any client, model, or runtime.

1. **Start LM Studio's server and load a model** with room for an agent's prompt, in LM Studio's
   Developer tab or with its `lms` command:

   ```bash
   lms server start --port 1234
   lms load <model-id> --context-length 32768
   ```

2. **Start AgentSplice.** [Installing AgentSplice](docs/INSTALLATION.md) covers Docker, the Windows and
   Linux packages, and running from source, and the first three carry the dashboard. From a clone, with
   the .NET 10 SDK:

   ```bash
   dotnet run --project src/AgentSplice.Api
   ```

3. **Check that it sees the model.** The `id` values listed are the model names a client sends:

   ```bash
   curl -s http://127.0.0.1:5280/v1/models
   ```

   ```powershell
   curl.exe -s http://127.0.0.1:5280/v1/models
   ```

4. **Point a client at `http://127.0.0.1:5280/v1`**, with any placeholder as its API key:

   | Client | Configuration |
   | --- | --- |
   | OpenCode | [`examples/opencode/opencode.json`](examples/opencode/opencode.json), with your model ID |
   | Cline | API Provider **OpenAI Compatible**, with the fields in [Cline](docs/CLIENTS.md#cline) |
   | Any client or SDK that speaks OpenAI Chat Completions | [Base URL, `curl`, PowerShell, and Python](docs/CLIENTS.md#any-openai-compatible-client) |

5. **Open the exchange.** A completion response carries `x-agentsplice-exchange-id`. Open
   `http://127.0.0.1:5280/dashboard/exchanges/<exchange-id>`, or the Exchanges list at
   `http://127.0.0.1:5280/dashboard/exchanges`. Run from source, the gateway serves the dashboard only
   once it is built and `--agentsplice:dashboard:rootPath` points at the build
   ([Development guide](docs/DEVELOPMENT.md), "The dashboard"); the same evidence is at
   `/api/v1/exchanges/<exchange-id>`.

[Pointing a client at AgentSplice](docs/CLIENTS.md) explains each setting and what to do when a request
fails.

## Why this project exists

Agent workflows fail across several independent boundaries: client schemas, provider adapters, prompt templates, streaming parsers, model behavior, tool-call representations, cache mechanisms, runtime versions, and hardware backends. The user interface often collapses all of those failures into “the model is slow” or “tools do not work.”

AgentSplice provides evidence instead of guesswork. It is intended to answer:

- What did the client actually send?
- What did the runtime actually receive?
- Which fields or events changed?
- Where was wall-clock time spent?
- Was a tool call structurally valid or merely printed as text?
- Was prefix reuse probable, absent, or unknown?
- Does the same request behave differently across models, runtimes, backends, or versions?
- Did an update introduce a correctness or performance regression?
- Can the behavior be replayed and attached to an upstream issue?

## Durable core versus optional adapters

The durable product core is:

1. transparent trace capture;
2. observability and timeline reconstruction;
3. sanitized replay;
4. protocol and behavioral conformance;
5. agent-task evaluation and regression testing;
6. portable evidence and OpenTelemetry export.

Tool-call recovery, Qwen/Laguna-specific rules, OpenAI/Anthropic translation, prompt/schema compaction, cache-log parsers, and runtime workarounds are optional, versioned adapters. The project must remain useful when those defects are fixed upstream.

See [Product positioning](docs/PRODUCT_POSITIONING.md) and [ADR 0006](docs/adr/0006-durable-core-and-adapter-lifecycle.md).

## Status

This repository is specification-first. Implementation is intentionally staged.

**Stage 0 — Repository foundation is complete.** The .NET solution exists with enforced module
boundaries, the domain model for exchanges and evidence, validated configuration, a deterministic
fake upstream runtime, and CI on Windows and Ubuntu.

**Stage 1A — Transparent request path is complete.** `GET /v1/models` and non-streaming
`POST /v1/chat/completions` are served against an LM Studio runtime. Requests are forwarded without
semantic rewriting, request correlation is returned on every response, latency boundaries are
recorded where they were observed, and OpenTelemetry spans and metrics are emitted through
`System.Diagnostics`.

**Stage 1B — Streaming correctness and timeline is complete.** A `stream: true` request is relayed
byte for byte as it arrives, with SSE framing observed rather than rebuilt, the first upstream byte,
first decoded event, first semantic output event, and first client flush recorded as four separate
boundaries, and every way a stream can end classified rather than collapsed. Ten correctness defects
found in three review passes over 1A and 1B are corrected in
[ADR 0010](docs/adr/0010-correct-stream-boundary-and-termination-semantics.md),
[ADR 0011](docs/adr/0011-per-event-bounds-and-media-type-parsing.md), and
[ADR 0012](docs/adr/0012-classification-independent-of-relayability.md).

**Stage 1C — Metadata persistence, administrative APIs, and the minimal dashboard is complete.**
Exchanges are written to a local SQLite store through a bounded queue and a background writer, swept
by retention, and served by the authenticated `/api/v1` surface and the dashboard that reads it;
`agentsplice:persistence:mode: None` keeps purely ephemeral operation available.

**Stage 1D — Local packaging and first public alpha is in progress.** The OpenTelemetry SDK now
subscribes to AgentSplice's spans and metrics, and `agentsplice:telemetry:otlp` exports them over OTLP
— off by default, never pointed anywhere implicitly, and with an optional collector in the Compose
file ([ADR 0014](docs/adr/0014-stage-1d-opentelemetry-export.md)). The gateway now serves the
dashboard itself at `/dashboard/`, and the container image builds it in, so an installation needs no
Node toolchain to open it ([ADR 0015](docs/adr/0015-stage-1d-gateway-serves-the-dashboard.md)).
The image reports its own health: it carries a small probe that its `HEALTHCHECK` runs against
`/health/live`, because the runtime base image has no HTTP client to run one with
([ADR 0016](docs/adr/0016-stage-1d-the-image-reports-its-health.md)). A self-contained Windows
package runs without Docker or a .NET installation, from any folder
([ADR 0017](docs/adr/0017-stage-1d-a-windows-package-that-needs-no-dotnet-sdk.md)), and a version
tag turns it into a draft pre-release for a maintainer to review and publish
([ADR 0018](docs/adr/0018-stage-1d-tagged-releases-as-reviewed-drafts.md)). OpenCode, Cline, and any
client that speaks OpenAI Chat Completions can be pointed at the gateway with the sample configuration
and guide in [docs/CLIENTS.md](docs/CLIENTS.md); the address, headers, settings, and error codes they
name are checked against the gateway's own
([ADR 0019](docs/adr/0019-stage-1d-sample-client-configurations-and-a-quickstart.md)).
AgentSplice is licensed under the Apache License 2.0, and every artifact carries the license and the
notices of what it bundles ([ADR 0020](docs/adr/0020-stage-1d-the-project-is-licensed-under-apache-2.md)).
A Linux package, the Windows package's twin for x64 distributions on glibc, ships beside it in every
release, and CI assembles a release's assets on every change
([ADR 0021](docs/adr/0021-stage-1d-a-linux-package-that-needs-no-dotnet-installation.md)).
A release carries the image as an archive, and publishing the release pushes exactly that image to
GitHub Container Registry, never under `latest`
([ADR 0022](docs/adr/0022-stage-1d-the-image-is-pushed-to-ghcr-when-a-release-is-published.md)). The
first dated trace report records OpenCode sessions through the Windows package to LM Studio: what the
gateway recorded, what that showed, and no compatibility claim
([ADR 0023](docs/adr/0023-stage-1d-the-first-dated-trace-report.md),
[docs/reports](docs/reports/README.md)).
Every build, package, and image runs on .NET 10 LTS, supported until November 2028, rather than on
.NET 8, whose support ends on November 10, 2026
([ADR 0024](docs/adr/0024-stage-1d-dotnet-10-lts-before-the-first-alpha.md)).
Replay, conformance orchestration, evaluation, protocol translation, and compatibility adapters all
belong to later milestones.

No compatibility claim is made for any client, model, or runtime. An HTTP 200 is not evidence of
compatibility, and support claims require a dated conformance report (`docs/CONFORMANCE.md`).

See [Development guide](docs/DEVELOPMENT.md) to build and test.

## Stack

In use today: ASP.NET Core 10 and C# 12, System.Text.Json, Server-Sent Events, `System.Diagnostics`
spans and metrics with the OpenTelemetry SDK and an opt-in OTLP exporter, SQLite, React, TypeScript,
and Vite for the dashboard, and xUnit with architecture, contract, and integration suites. The rest is
proposed and arrives with the stage that needs it.

- ASP.NET Core 10 and C# 12
- System.Text.Json
- Server-Sent Events
- OpenTelemetry
- SQLite for local mode
- PostgreSQL for shared/self-hosted mode
- xUnit, architecture tests, integration tests, and Testcontainers
- Docker and Docker Compose
- React, TypeScript, and Vite for the optional local dashboard

## Protocol endpoints

Served today:

```http
GET  /v1/models
POST /v1/chat/completions      # non-streaming and SSE
```

The administrative APIs under `/api/v1` expose health, exchanges, timeline metadata, and runtime diagnostics, and `/health/live` and `/health/ready` serve orchestrators ([API notes](docs/API.md)). Content retention remains disabled by default: what is stored is structural metadata — counts, shapes, timings, and field names — and never prompts, model output, tool arguments, or credentials.

The dashboard is served by the gateway at `http://127.0.0.1:5280/dashboard/`, and `/` redirects there, whenever a build is present — always in the container image. With Docker:

```bash
cp .env.example .env
echo "AGENTSPLICE_ADMIN_API_KEY=$(openssl rand -hex 32)" >> .env
docker compose up --build --wait
# open http://127.0.0.1:5280/ and paste the token from .env into the dashboard's token field
```

The same in PowerShell:

```powershell
Copy-Item .env.example .env
$bytes = [byte[]]::new(32); [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
Add-Content .env ("AGENTSPLICE_ADMIN_API_KEY=" + (($bytes | ForEach-Object { $_.ToString('x2') }) -join ''))
docker compose up --build --wait
```

`--wait` returns once the image's health check reports the gateway healthy, and `docker compose ps`
shows that status afterwards. Once a release is published, its image needs no clone at all:
`ghcr.io/murillojndem/agent-splice:<version>`, started with the `docker run` command in
[Installing AgentSplice](docs/INSTALLATION.md).

Without Docker, the Windows and Linux packages need no .NET installation: download
`agentsplice-<version>-win-x64.zip` or `agentsplice-<version>-linux-x64.tar.gz`, and `SHA256SUMS`, from
the releases page, check the digest, extract it, and run `AgentSplice.Api.exe` or `AgentSplice.Api` from
any folder. Each listens on loopback, carries the dashboard, and keeps its store beside the executable.
[Installing AgentSplice](docs/INSTALLATION.md) covers every path — Docker, the two packages, and running
from source — step by step.

## Documentation

- [Installing AgentSplice](docs/INSTALLATION.md)
- [Pointing a client at AgentSplice](docs/CLIENTS.md)
- [Development guide](docs/DEVELOPMENT.md)
- [Complete product and engineering specification](docs/SPECIFICATION.md)
- [Product positioning](docs/PRODUCT_POSITIONING.md)
- [Architecture](docs/ARCHITECTURE.md)
- [Roadmap](docs/ROADMAP.md)
- [Conformance specification](docs/CONFORMANCE.md)
- [Replay specification](docs/REPLAY.md)
- [Benchmarking and evaluation](docs/BENCHMARKING.md)
- [Observability](docs/OBSERVABILITY.md)
- [API contract notes](docs/API.md)
- [Claude implementation contract](CLAUDE.md)
- [Threat model](docs/THREAT_MODEL.md)
- [Portfolio brief](docs/PORTFOLIO_BRIEF.md)

## Design principles

1. Evidence before workaround.
2. Transparent capture before transformation.
3. Replay and conformance before broad compatibility rewriting.
4. Local-first privacy and loopback-only defaults.
5. Never execute model-requested tools inside the core gateway.
6. Distinguish measured, reported, inferred, and estimated values.
7. Preserve streaming and cancellation semantics.
8. Treat transformations as explicit, versioned, auditable policies.
9. Treat AMD/ROCm and other non-CUDA environments as first-class evidence sources.
10. Upstream fixes when a defect belongs in another project.

## Product boundary

AgentSplice is not another chat, coding agent, model runtime, model downloader, general cloud billing router, or permanent workaround collection. It is an independent diagnostic and evaluation plane for agent/runtime interactions.

## License

AgentSplice is licensed under the [Apache License 2.0](LICENSE); see [NOTICE](NOTICE). The software it bundles is
listed with its licenses in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Report vulnerabilities privately, as
[SECURITY.md](SECURITY.md) describes.
