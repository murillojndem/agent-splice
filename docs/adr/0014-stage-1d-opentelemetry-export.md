# ADR 0014 — Stage 1D: the OpenTelemetry SDK and OTLP export

- Status: Accepted
- Date: 2026-09-30
- Implements decision 16 of [ADR 0013](0013-stage-1c-metadata-store.md), which moved the SDK to this
  stage; discharges decision 14 of [ADR 0008](0008-stage-1a-transparent-request-path.md) and decision
  12 of [ADR 0009](0009-stage-1b-streaming-relay.md), which deferred it before that

## Context

Since Stage 1A, AgentSplice has emitted spans and metrics through `System.Diagnostics` and subscribed
to its own activity sources with a hand-registered `ActivityListener`. That listener existed only so
that spans existed at all — without a listener `StartActivity` returns null and
`x-agentsplice-trace-id` is never populated. Nothing left the process. FR-OBS-001 and FR-OBS-002 ask for
OpenTelemetry traces and metrics, and `docs/SPECIFICATION.md` section 20.2 draws an optional
OpenTelemetry Collector in the Compose topology.

The SDK was deferred three times, each time for the same reason: what it adds is an exporter, and no
exporter was configured. Stage 1D is packaging, and an exporter is part of what gets packaged.

Meanwhile `.env.example` carried `OTEL_EXPORTER_OTLP_ENDPOINT=http://otel-collector:4317`. Nothing read
it, the Compose file did not pass it to the gateway, and no `otel-collector` service existed. It read
as a working setting.

## Decisions

### 1. The SDK replaces the listener, and runs whether or not anything is exported

`AddAgentSpliceObservability` registers the SDK's tracer and meter providers for the four live
sources and the `AgentSplice` meter, and the hand-registered listener is deleted. The SDK is not added
beside it: two listeners on one source both take part in every sampling decision, and the one that
does not export would be a second, silent opinion on which spans exist. An architecture test now
fails if any production assembly — the API included — depends on `ActivityListener`.

The providers run with export off, because the header needs the span either way. `ExchangeTelemetry`
takes the `TracerProvider` as a constructor dependency, which is what the listener dependency did
before it: resolving it builds the subscription, so spans exist before the first one is started as a
matter of construction order rather than registration order. The W3C identifier format is still
forced explicitly; the SDK happens to force it too, and the header's format should not depend on that.

Sampling is `AlwaysOnSampler`. The SDK's default is parent-based, which would let a client that sends
a `traceparent` marked unsampled discard the spans for its own exchanges. Sampling is a volume control
for a hosted service; a local-first gateway whose product is evidence keeps every exchange, which is
what the listener did.

### 2. Export is off by default, and the destination is never defaulted

```yaml
agentsplice:
  telemetry:
    otlp:
      enabled: false          # default
      endpoint: http://127.0.0.1:4318
      protocol: HttpProtobuf  # default; or Grpc
```

Exported telemetry carries no content, but it describes every exchange — which runtime served it, how
each phase went, how it ended — and a stream of that leaving the process is a decision. So export is
off unless `enabled` is true, and turning it on without an `endpoint` fails startup rather than falling
back to the SDK's `localhost` default. `/api/v1/system` reports `telemetryExportEnabled` beside the two
retention flags, because exported telemetry is a third copy of what each exchange looked like. It
reports the fact and not the destination, which is configuration the operator already holds.

Startup validation refuses an endpoint that is not absolute `http` or `https`, that embeds
credentials, that carries a query or fragment, that carries a path when the protocol is gRPC, or that
names a signal path (`/v1/traces`) where the receiver's base address belongs. A configured endpoint is
validated even while export is off, as a disabled runtime's base URL is. Every one of these rules
guards the same failure: telemetry sent somewhere other than where the operator believes, with nothing
on the request path able to notice — decision 6 is why.

### 3. The standard endpoint variable neither turns export on nor redirects it

The exporters are constructed directly, with the endpoint and protocol set from AgentSplice's
settings, rather than through `AddOtlpExporter` or `UseOtlpExporter`, which take them from the host's
configuration. `OTEL_EXPORTER_OTLP_ENDPOINT` and `OTEL_EXPORTER_OTLP_PROTOCOL` therefore have no effect.
A variable left over from another tool in a developer's shell is not consent, and an integration test
sets both to point at a live receiver and asserts that nothing arrives.

The SDK still reads its remaining standard variables when the exporter options are created, and that
is useful rather than a leak: `OTEL_EXPORTER_OTLP_HEADERS` is how a collector credential is supplied
without entering AgentSplice's configuration or its log, and `OTEL_EXPORTER_OTLP_TIMEOUT` bounds each
export. Neither can change where telemetry goes.

### 4. AgentSplice computes each signal's address

The SDK appends `v1/traces` to an OTLP/HTTP endpoint only when it chose the endpoint itself; an
endpoint set in code is used verbatim. Handing it the receiver's base address would post spans to the
collector's root and be answered 404. `OtlpExportTarget` appends `v1/traces` and `v1/metrics` after any
path the base address already has, so a collector behind a reverse proxy at `/otlp` receives spans at
`/otlp/v1/traces`. A gRPC endpoint is passed through; the exporter supplies the method path.

### 5. OTLP/HTTP is the default; gRPC is supported

`HttpProtobuf` is the transport the OpenTelemetry specification recommends as an SDK default, and it
needs nothing beyond HTTP/1.1 between gateway and collector. The .NET SDK's own default is gRPC for
historical reasons, which is exactly why the protocol is an explicit AgentSplice setting rather than
inherited. Both transports are proven against a real receiver in the integration suite — gRPC over
cleartext HTTP/2 with prior knowledge, which is what an `http://` gRPC endpoint means.

### 6. Export never touches the request path

Spans leave through the SDK's batch processor and metrics through its periodic reader, each on its own
thread. The batch queue is bounded and drops when full. An unreachable collector costs a failed export
on a background thread; an integration test points export at a closed port and asserts the exchange
completes unchanged.

The price is visibility. The SDK reports exporter failures through its own `EventSource`
(`OpenTelemetry-Exporter-OpenTelemetryProtocol`), not through the gateway's log, so an export that is
failing is silent unless SDK self-diagnostics are enabled. The mitigations here are all before the
fact — endpoint validation, a single startup line (event `1202 TelemetryExportEnabled`) naming the
transport and destination, and a Compose default that points at a collector that exists. A rate-limited
bridge from that `EventSource` into the gateway log is the follow-up that closes the gap.

### 7. Traces and metrics only — no logs, no framework instrumentation

Logs are not routed through OpenTelemetry. The log pipeline is the one FR-OBS-007 constrains, its
redaction is tested where logs are written, and exporting it is a separate decision. A test asserts
that no OpenTelemetry logger provider is registered, so one arriving as a side effect of a package
upgrade fails the build.

The ASP.NET Core and `HttpClient` instrumentation packages are not added. They would emit spans and
attributes this slice has not reviewed against `docs/OBSERVABILITY.md` — the administrative API's
request paths among them — and a span for every health probe, which is noise at
best and, under `AlwaysOnSampler`, a steady export load that carries no exchange evidence.

### 8. The Compose collector is opt-in and publishes nothing on the host

An `otel-collector` service (`otel/opentelemetry-collector:0.162.0`, pinned) runs only with the `otel`
profile. `config/otel-collector.yaml` opens both OTLP transports on `0.0.0.0` inside the container —
the collector's own default is `localhost`, which would refuse the gateway's container — and prints
what arrives through the `debug` exporter. It publishes no port on the host. As shipped it is a way to
see exactly what AgentSplice exports; keeping the data means adding an exporter for a backend.

The gateway's Compose defaults point at it (`http://otel-collector:4318`, `HttpProtobuf`) with export
off. The stale `OTEL_EXPORTER_OTLP_ENDPOINT` line is removed from `.env.example`, and a contract test
fails if either deployment file sets that variable again.

## Consequences

- `AgentSplice.Observability` is the only production project that references OpenTelemetry, enforced
  by an architecture test; a second test forbids the API from configuring telemetry itself.
- OpenTelemetry 1.19.1 requires `System.Diagnostics.DiagnosticSource` 10.0 on `net8.0`, so the
  published application carries that assembly and it takes precedence over the shared framework's 8.0
  copy. This is the SDK's supported configuration on .NET 8.
- Exported resources carry `service.name=agentsplice`, the build's informational version as
  `service.version`, and a generated `service.instance.id`.
- Spans reach a collector within the batch processor's five-second schedule. Metrics are exported on
  the SDK's default sixty-second interval, which is not yet configurable.
- **Known limitation: the exchange span's parent is not exported.** ASP.NET Core creates an activity
  for every request, and `agentsplice.exchange` starts as its child. That activity belongs to a source
  AgentSplice does not subscribe to (decision 7), so a backend shows the exchange span with a parent
  it never received, and when a client propagated a `traceparent` the link to the client's span runs
  through that missing span. The trace identifier is unaffected, so `x-agentsplice-trace-id` still
  finds the exchange. Re-parenting the exchange span onto the incoming context is a change to trace
  structure and deserves its own decision.
- Measured against the fake collector and against a real `otel/opentelemetry-collector:0.162.0` on a
  development workstation. Neither is a performance claim.

## Alternatives considered

- **`UseOtlpExporter` with configuration binding.** It appends signal paths correctly and is a single
  call, but it takes its endpoint from the host's configuration, including `OTEL_EXPORTER_OTLP_ENDPOINT`,
  and applies to every signal the builder has. Rejected for decision 3.
- **Honouring `OTEL_EXPORTER_OTLP_ENDPOINT` as the switch.** The conventional OpenTelemetry experience,
  and wrong for a gateway whose telemetry describes every exchange: the variable is commonly set
  globally by other tooling, and its presence is not an operator's decision about AgentSplice.
- **Keeping the listener and adding exporters beside the SDK.** Rejected in decision 1.
- **Exporting logs.** Deferred rather than rejected; it needs its own review of what a log record
  carries once it leaves the process.
